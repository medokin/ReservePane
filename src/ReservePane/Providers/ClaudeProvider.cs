using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ReservePane.Model;

namespace ReservePane.Providers;

public sealed class ClaudeProvider : IStatusProvider, IProviderAvailability
{
    private static readonly Uri UsageUri = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly Uri ProfileUri = new("https://api.anthropic.com/api/oauth/profile");
    private readonly string _credentialPath;
    private readonly HttpMessageHandler _handler;
    private readonly Func<double?, Severity> _severityFromPercent;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, Stream> _openCredential;
    private readonly Func<string, bool> _credentialProbe;
    private readonly Func<CancellationToken, Task<bool>> _refreshCredential;
    private DateTimeOffset _profileCachedAt;
    private string? _cachedPlanLabel;

    public ClaudeProvider(
        string credentialPath,
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider = null)
        : this(
            credentialPath,
            handler,
            severityFromPercent,
            timeProvider,
            OpenCredentialStream,
            CredentialFilePrerequisite.Probe,
            ClaudeCredentialRefresher.RefreshAsync)
    {
    }

    internal ClaudeProvider(
        string credentialPath,
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider,
        Func<string, Stream> openCredential)
        : this(
            credentialPath,
            handler,
            severityFromPercent,
            timeProvider,
            openCredential,
            CredentialFilePrerequisite.Probe,
            _ => Task.FromResult(false))
    {
    }

    internal ClaudeProvider(
        string credentialPath,
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider,
        Func<string, Stream> openCredential,
        Func<string, bool> credentialProbe)
        : this(
            credentialPath,
            handler,
            severityFromPercent,
            timeProvider,
            openCredential,
            credentialProbe,
            _ => Task.FromResult(false))
    {
    }

    internal ClaudeProvider(
        string credentialPath,
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider,
        Func<string, Stream> openCredential,
        Func<string, bool> credentialProbe,
        Func<CancellationToken, Task<bool>> refreshCredential)
    {
        _credentialPath = credentialPath;
        _handler = handler;
        _severityFromPercent = severityFromPercent;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _openCredential = openCredential;
        _credentialProbe = credentialProbe;
        _refreshCredential = refreshCredential;
    }

    public string Id => "claude";

    public string Label => "Claude";

    public TimeSpan MinimumRefreshInterval => TimeSpan.FromMinutes(5);

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        CredentialFilePrerequisite.IsPresentOrIndeterminateAsync(
            _credentialPath,
            _credentialProbe,
            cancellationToken);

    internal static Stream OpenCredentialStream(string credentialPath) =>
        new FileStream(
            credentialPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.SequentialScan);

    public Task<ProviderFetchResult> FetchAsync(CancellationToken cancellationToken) =>
        FetchAsync(allowRefresh: true, credential: null, cancellationToken);

    private async Task<ProviderFetchResult> FetchAsync(
        bool allowRefresh,
        Credential? credential,
        CancellationToken cancellationToken)
    {
        DateTimeOffset fetchedAt = _timeProvider.GetUtcNow();
        if (credential is null)
        {
            try
            {
                credential = ReadCredential();
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return new ProviderFetchResult(
                    ProviderFetchOutcome.NotConfigured,
                    Snapshot(HealthState.Unreachable, null, [], [], null, fetchedAt));
            }
        }

        if (credential.ExpiresAt <= fetchedAt)
        {
            Credential? refreshed = allowRefresh
                ? await TryRefreshCredentialAsync(credential, cancellationToken).ConfigureAwait(false)
                : null;
            if (refreshed is not null)
            {
                return await FetchAsync(allowRefresh: false, refreshed, cancellationToken).ConfigureAwait(false);
            }

            return new ProviderFetchResult(
                ProviderFetchOutcome.AuthenticationRequired,
                Snapshot(HealthState.AuthExpired, null, [], [], "re-auth: run claude login", fetchedAt));
        }

        using var client = new HttpClient(_handler, disposeHandler: false);
        using HttpResponseMessage usageResponse = await SendAsync(client, UsageUri, credential.AccessToken, cancellationToken)
            .ConfigureAwait(false);
        if (IsAuthExpired(usageResponse.StatusCode))
        {
            Credential? refreshed = allowRefresh
                ? await TryRefreshCredentialAsync(credential, cancellationToken).ConfigureAwait(false)
                : null;
            if (refreshed is not null)
            {
                usageResponse.Dispose();
                return await FetchAsync(allowRefresh: false, refreshed, cancellationToken).ConfigureAwait(false);
            }

            return new ProviderFetchResult(
                ProviderFetchOutcome.AuthenticationRequired,
                Snapshot(HealthState.AuthExpired, null, [], [], "re-auth: run claude login", fetchedAt),
                usageResponse.StatusCode);
        }

        TimeSpan? retryAfter = ProviderHttpSafety.GetRetryAfter(usageResponse, fetchedAt);
        if (retryAfter is not null)
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.RateLimited,
                statusCode: usageResponse.StatusCode,
                retryAfter: retryAfter);
        }

        if (!usageResponse.IsSuccessStatusCode)
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.TransientFailure,
                statusCode: usageResponse.StatusCode);
        }

        JsonDocument usage;
        try
        {
            usage = await ProviderHttpSafety.ReadJsonAsync(usageResponse, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.InvalidResponse,
                statusCode: usageResponse.StatusCode);
        }

        using (usage)
        {
        ImmutableArray<UsageWindow> windows = ReadWindows(usage.RootElement);
        ImmutableArray<InfoLine> info = ReadSpend(usage.RootElement);
        ProfileResult profile = await GetProfileAsync(client, credential.AccessToken, fetchedAt, cancellationToken)
            .ConfigureAwait(false);
        if (profile.AuthExpired)
        {
            Credential? refreshed = allowRefresh
                ? await TryRefreshCredentialAsync(credential, cancellationToken).ConfigureAwait(false)
                : null;
            if (refreshed is not null)
            {
                return await FetchAsync(allowRefresh: false, refreshed, cancellationToken).ConfigureAwait(false);
            }

            return new ProviderFetchResult(
                ProviderFetchOutcome.AuthenticationRequired,
                Snapshot(
                    HealthState.AuthExpired,
                    profile.PlanLabel,
                    windows,
                    info,
                    "re-auth: run claude login",
                    fetchedAt),
                profile.StatusCode);
        }

        if (profile.Error is not null)
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.PartialSuccess,
                Snapshot(
                    HealthState.Degraded,
                    profile.PlanLabel,
                    windows,
                    info,
                    profile.Error,
                    fetchedAt),
                profile.StatusCode);
        }

        return new ProviderFetchResult(
            ProviderFetchOutcome.Success,
            Snapshot(
                HealthState.Ok,
                profile.PlanLabel,
                windows,
                info,
                null,
                fetchedAt),
            usageResponse.StatusCode);
        }
    }

    private async Task<Credential?> TryRefreshCredentialAsync(
        Credential previous,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await _refreshCredential(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            Credential refreshed = ReadCredential();
            return !string.Equals(refreshed.AccessToken, previous.AccessToken, StringComparison.Ordinal) &&
                refreshed.ExpiresAt > _timeProvider.GetUtcNow()
                    ? refreshed
                    : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private Credential ReadCredential()
    {
        using Stream stream = _openCredential(_credentialPath);
        return new CredentialScanner(stream).Read();
    }

    private async Task<ProfileResult> GetProfileAsync(
        HttpClient client,
        string accessToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_profileCachedAt.AddHours(1) > now)
        {
            return new ProfileResult(_cachedPlanLabel, false, null, null);
        }

        try
        {
            using HttpResponseMessage response = await SendAsync(client, ProfileUri, accessToken, cancellationToken)
                .ConfigureAwait(false);
            if (IsAuthExpired(response.StatusCode))
            {
                return new ProfileResult(_cachedPlanLabel, true, null, response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ProfileResult(
                    _cachedPlanLabel,
                    false,
                    "Claude profile request failed",
                    response.StatusCode);
            }

            JsonDocument profile;
            try
            {
                profile = await ProviderHttpSafety
                    .ReadJsonAsync(response, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return new ProfileResult(
                    _cachedPlanLabel,
                    false,
                    "Claude profile response was not JSON",
                    response.StatusCode);
            }

            using (profile)
            {
            _cachedPlanLabel = TryGetObject(profile.RootElement, "organization") is JsonElement organization
                ? TryGetString(organization, "seat_tier")
                : null;
            _profileCachedAt = now;
            return new ProfileResult(_cachedPlanLabel, false, null, response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ProfileResult(_cachedPlanLabel, false, "Claude profile request failed", null);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Uri uri,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private ImmutableArray<UsageWindow> ReadWindows(JsonElement root)
    {
        if (!TryGetProperty(root, "limits", out JsonElement limits) || limits.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var windows = ImmutableArray.CreateBuilder<UsageWindow>();
        foreach (JsonElement limit in limits.EnumerateArray())
        {
            if (limit.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            double? percent = TryGetDouble(limit, "percent");
            windows.Add(new UsageWindow(
                TryGetString(limit, "group") ?? TryGetString(limit, "kind") ?? "usage",
                percent,
                TryGetDateTimeOffset(limit, "resets_at"),
                TryGetSeverity(limit, "severity") ?? _severityFromPercent(percent)));
        }

        return windows.ToImmutable();
    }

    private static ImmutableArray<InfoLine> ReadSpend(JsonElement root)
    {
        if (TryGetObject(root, "spend") is not JsonElement spend ||
            TryGetObject(spend, "used") is not JsonElement used ||
            ReadMoney(used) is not string amount)
        {
            return [];
        }

        string budget = !spend.TryGetProperty("limit", out JsonElement limit) || limit.ValueKind == JsonValueKind.Null
            ? "no cap set"
            : ReadMoney(limit) ?? "Unavailable";
        return [new InfoLine("Spend", amount), new InfoLine("Budget", budget)];
    }

    private static string? ReadMoney(JsonElement money)
    {
        if (!TryGetProperty(money, "amount_minor", out JsonElement amountElement) ||
            amountElement.ValueKind != JsonValueKind.Number ||
            !amountElement.TryGetDecimal(out decimal amountMinor) ||
            TryGetString(money, "currency") is not string currency ||
            !TryGetProperty(money, "exponent", out JsonElement exponentElement) ||
            exponentElement.ValueKind != JsonValueKind.Number ||
            !exponentElement.TryGetInt32(out int exponent) ||
            exponent is < 0 or > 28)
        {
            return null;
        }

        decimal divisor = 1;
        for (int index = 0; index < exponent; index++)
        {
            divisor *= 10;
        }

        string amount = (amountMinor / divisor).ToString($"F{exponent}", CultureInfo.InvariantCulture);
        return $"{currency} {amount}";
    }

    private static bool IsAuthExpired(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value);
    }

    private static JsonElement? TryGetObject(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? TryGetDouble(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.TryGetDouble(out double result)
            ? result
            : null;

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        string? value = TryGetString(element, propertyName);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset result)
            ? result
            : null;
    }

    private static Severity? TryGetSeverity(JsonElement element, string propertyName) =>
        TryGetString(element, propertyName)?.ToLowerInvariant() switch
        {
            "normal" => Severity.Normal,
            "warning" => Severity.Warning,
            "critical" => Severity.Critical,
            _ => null
        };

    private static ProviderSnapshot Snapshot(
        HealthState health,
        string? planLabel,
        ImmutableArray<UsageWindow> windows,
        ImmutableArray<InfoLine> info,
        string? error,
        DateTimeOffset fetchedAt) =>
        new("claude", "Claude", health, planLabel, windows, info, error, fetchedAt, 0);

    private sealed record Credential(string AccessToken, DateTimeOffset ExpiresAt);

    private sealed record ProfileResult(
        string? PlanLabel,
        bool AuthExpired,
        string? Error,
        HttpStatusCode? StatusCode);

    private sealed class CredentialScanner(Stream stream)
    {
        private const int MaxUnknownContainerDepth = 64;
        private static readonly byte[] OAuthProperty = "claudeAiOauth"u8.ToArray();
        private static readonly byte[] AccessTokenProperty = "accessToken"u8.ToArray();
        private static readonly byte[] ExpiresAtProperty = "expiresAt"u8.ToArray();
        private readonly Stream _stream = stream;
        private int _lookahead = -1;

        public Credential Read()
        {
            Expect((byte)'{');
            while (true)
            {
                int next = ReadNonWhitespace();
                if (next == '}')
                {
                    throw Incomplete();
                }

                Unread(next);
                bool isOAuth = ReadPropertyNameMatches(OAuthProperty);
                Expect((byte)':');
                if (isOAuth)
                {
                    return ReadOAuthObject();
                }

                SkipValue();
                ReadObjectSeparator();
            }
        }

        private Credential ReadOAuthObject()
        {
            Expect((byte)'{');
            string? accessToken = null;
            long? expiresAtUnixMilliseconds = null;

            while (true)
            {
                int next = ReadNonWhitespace();
                if (next == '}')
                {
                    throw Incomplete();
                }

                Unread(next);
                CredentialField property = ReadOAuthProperty();
                Expect((byte)':');

                if (property == CredentialField.AccessToken)
                {
                    if (accessToken is not null || ReadNonWhitespace() != '"')
                    {
                        throw Incomplete();
                    }

                    Unread('"');
                    accessToken = ReadExpectedString();
                }
                else if (property == CredentialField.ExpiresAt)
                {
                    if (expiresAtUnixMilliseconds is not null)
                    {
                        throw Incomplete();
                    }

                    expiresAtUnixMilliseconds = ReadExpectedInt64();
                }
                else
                {
                    SkipValue();
                }

                if (accessToken is not null && expiresAtUnixMilliseconds is long expiresAt)
                {
                    if (string.IsNullOrWhiteSpace(accessToken))
                    {
                        throw Incomplete();
                    }

                    return new Credential(accessToken, DateTimeOffset.FromUnixTimeMilliseconds(expiresAt));
                }

                ReadObjectSeparator();
            }
        }

        private CredentialField ReadOAuthProperty()
        {
            Expect((byte)'"');
            int length = 0;
            bool accessTokenMatches = true;
            bool expiresAtMatches = true;

            while (true)
            {
                int value = ReadPropertyCharacter();
                if (value == -1)
                {
                    if (accessTokenMatches && length == AccessTokenProperty.Length)
                    {
                        return CredentialField.AccessToken;
                    }

                    return expiresAtMatches && length == ExpiresAtProperty.Length
                        ? CredentialField.ExpiresAt
                        : CredentialField.None;
                }

                if (value < 0 || length >= AccessTokenProperty.Length || value != AccessTokenProperty[length])
                {
                    accessTokenMatches = false;
                }

                if (value < 0 || length >= ExpiresAtProperty.Length || value != ExpiresAtProperty[length])
                {
                    expiresAtMatches = false;
                }

                length++;
            }
        }

        private bool ReadPropertyNameMatches(ReadOnlySpan<byte> expected)
        {
            Expect((byte)'"');
            int index = 0;
            bool matches = true;

            while (true)
            {
                int value = ReadPropertyCharacter();
                if (value == -1)
                {
                    return matches && index == expected.Length;
                }

                if (value < 0 || index >= expected.Length || value != expected[index])
                {
                    matches = false;
                }

                index++;
            }
        }

        private string ReadExpectedString()
        {
            byte[] encoded = ArrayPool<byte>.Shared.Rent(128);
            int length = 0;
            bool escaped = false;

            try
            {
                Expect((byte)'"');
                Append((byte)'"');
                while (true)
                {
                    int value = ReadByte();
                    if (value < 0)
                    {
                        throw Incomplete();
                    }

                    Append((byte)value);
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (value == '\\')
                    {
                        escaped = true;
                        continue;
                    }

                    if (value == '"')
                    {
                        var reader = new Utf8JsonReader(encoded.AsSpan(0, length), isFinalBlock: true, state: default);
                        if (!reader.Read() || reader.TokenType != JsonTokenType.String || reader.GetString() is not string token)
                        {
                            throw Incomplete();
                        }

                        return token;
                    }

                    if (value < 0x20)
                    {
                        throw Incomplete();
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
                ArrayPool<byte>.Shared.Return(encoded);
            }

            void Append(byte value)
            {
                if (length == encoded.Length)
                {
                    byte[] expanded = ArrayPool<byte>.Shared.Rent(encoded.Length * 2);
                    Buffer.BlockCopy(encoded, 0, expanded, 0, length);
                    CryptographicOperations.ZeroMemory(encoded);
                    ArrayPool<byte>.Shared.Return(encoded);
                    encoded = expanded;
                }

                encoded[length++] = value;
            }
        }

        private long ReadExpectedInt64()
        {
            int first = ReadNonWhitespace();
            bool negative = first == '-';
            if (negative)
            {
                first = ReadByte();
            }

            if (first == '0')
            {
                ReadIntegerDelimiter();
                return 0;
            }

            if (first is < '1' or > '9')
            {
                throw Incomplete();
            }

            ulong maximum = negative ? (ulong)long.MaxValue + 1 : long.MaxValue;
            ulong value = (uint)(first - '0');
            while (true)
            {
                int next = ReadByte();
                if (next is >= '0' and <= '9')
                {
                    uint digit = (uint)(next - '0');
                    if (value > (maximum - digit) / 10)
                    {
                        throw Incomplete();
                    }

                    value = (value * 10) + digit;
                    continue;
                }

                Unread(next);
                ReadIntegerDelimiter();
                if (!negative)
                {
                    return (long)value;
                }

                return value == (ulong)long.MaxValue + 1 ? long.MinValue : -(long)value;
            }
        }

        private void SkipValue(int depth = 0)
        {
            int first = ReadNonWhitespace();
            switch (first)
            {
                case '"':
                    SkipStringBody();
                    return;
                case '{':
                    if (depth >= MaxUnknownContainerDepth)
                    {
                        throw Incomplete();
                    }

                    SkipObject(depth + 1);
                    return;
                case '[':
                    if (depth >= MaxUnknownContainerDepth)
                    {
                        throw Incomplete();
                    }

                    SkipArray(depth + 1);
                    return;
                case 't':
                    ReadLiteral("rue"u8);
                    return;
                case 'f':
                    ReadLiteral("alse"u8);
                    return;
                case 'n':
                    ReadLiteral("ull"u8);
                    return;
                default:
                    if (first is '-' or >= '0' and <= '9')
                    {
                        SkipNumber();
                        return;
                    }

                    throw Incomplete();
            }
        }

        private void SkipObject(int depth)
        {
            int next = ReadNonWhitespace();
            if (next == '}')
            {
                return;
            }

            Unread(next);
            while (true)
            {
                SkipString();
                Expect((byte)':');
                SkipValue(depth);
                int separator = ReadNonWhitespace();
                if (separator == '}')
                {
                    return;
                }

                if (separator != ',')
                {
                    throw Incomplete();
                }
            }
        }

        private void SkipArray(int depth)
        {
            int next = ReadNonWhitespace();
            if (next == ']')
            {
                return;
            }

            Unread(next);
            while (true)
            {
                SkipValue(depth);
                int separator = ReadNonWhitespace();
                if (separator == ']')
                {
                    return;
                }

                if (separator != ',')
                {
                    throw Incomplete();
                }
            }
        }

        private void SkipString()
        {
            Expect((byte)'"');
            SkipStringBody();
        }

        private void SkipStringBody()
        {
            bool escaped = false;
            while (true)
            {
                int value = ReadByte();
                if (value < 0 || value < 0x20)
                {
                    throw Incomplete();
                }

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (value == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (value == '"')
                {
                    return;
                }
            }
        }

        private void SkipNumber()
        {
            while (true)
            {
                int next = ReadByte();
                if (IsValueDelimiter(next))
                {
                    Unread(next);
                    return;
                }

                if (next < 0)
                {
                    throw Incomplete();
                }
            }
        }

        private void ReadLiteral(ReadOnlySpan<byte> literal)
        {
            foreach (byte expected in literal)
            {
                if (ReadByte() != expected)
                {
                    throw Incomplete();
                }
            }
        }

        private void ReadObjectSeparator()
        {
            int separator = ReadNonWhitespace();
            if (separator != ',')
            {
                throw Incomplete();
            }
        }

        private void Expect(byte expected)
        {
            if (ReadNonWhitespace() != expected)
            {
                throw Incomplete();
            }
        }

        private int ReadNonWhitespace()
        {
            int value;
            do
            {
                value = ReadByte();
            }
            while (value is ' ' or '\t' or '\r' or '\n');

            if (value < 0)
            {
                throw Incomplete();
            }

            return value;
        }

        private int ReadPropertyCharacter()
        {
            int value = ReadByte();
            if (value < 0 || value < 0x20)
            {
                throw Incomplete();
            }

            if (value == '"')
            {
                return -1;
            }

            if (value != '\\')
            {
                return value;
            }

            int escape = ReadByte();
            return escape switch
            {
                '"' => '"',
                '\\' => '\\',
                '/' => '/',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'u' => ReadUnicodeEscape(),
                _ => throw Incomplete()
            };
        }

        private int ReadUnicodeEscape()
        {
            int value = 0;
            for (int index = 0; index < 4; index++)
            {
                int digit = ReadByte();
                value = digit switch
                {
                    >= '0' and <= '9' => (value * 16) + digit - '0',
                    >= 'a' and <= 'f' => (value * 16) + digit - 'a' + 10,
                    >= 'A' and <= 'F' => (value * 16) + digit - 'A' + 10,
                    _ => throw Incomplete()
                };
            }

            return value <= byte.MaxValue ? value : -2;
        }

        private void ReadIntegerDelimiter()
        {
            int delimiter = ReadByte();
            while (delimiter is ' ' or '\t' or '\r' or '\n')
            {
                delimiter = ReadByte();
            }

            if (delimiter is not ',' and not '}')
            {
                throw Incomplete();
            }

            Unread(delimiter);
        }

        private int ReadByte()
        {
            if (_lookahead >= 0)
            {
                int value = _lookahead;
                _lookahead = -1;
                return value;
            }

            return _stream.ReadByte();
        }

        private void Unread(int value)
        {
            if (value < 0 || _lookahead >= 0)
            {
                throw Incomplete();
            }

            _lookahead = value;
        }

        private static bool IsValueDelimiter(int value) =>
            value is ',' or '}' or ']' or ' ' or '\t' or '\r' or '\n';

        private static InvalidDataException Incomplete() => new("Claude credentials are incomplete.");
    }

    private enum CredentialField { None, AccessToken, ExpiresAt }
}
