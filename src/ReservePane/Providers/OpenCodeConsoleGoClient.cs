using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text.Json;
using ReservePane.Model;

namespace ReservePane.Providers;

internal enum OpenCodeConsoleFetchOutcome
{
    Success,
    AuthenticationRequired,
    TransientFailure,
    RateLimited,
    InvalidResponse,
}

internal sealed record OpenCodeConsoleFetchResult(
    OpenCodeConsoleFetchOutcome Outcome,
    ImmutableArray<UsageWindow> Windows,
    HttpStatusCode? StatusCode = null,
    TimeSpan? RetryAfter = null);

internal interface IOpenCodeConsoleGoClient
{
    Task<OpenCodeConsoleFetchResult> FetchAsync(
        OpenCodeConsoleActiveWorkspace workspace,
        CancellationToken cancellationToken);
}

internal sealed class OpenCodeConsoleGoClient : IOpenCodeConsoleGoClient
{
    private static readonly Uri GoStatusUri = new("https://opencode.ai/console/api/go/status");

    private readonly HttpMessageHandler _handler;
    private readonly Func<double?, Severity> _severityFromPercent;
    private readonly TimeProvider _timeProvider;

    public OpenCodeConsoleGoClient(
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider = null)
    {
        _handler = handler;
        _severityFromPercent = severityFromPercent;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OpenCodeConsoleFetchResult> FetchAsync(
        OpenCodeConsoleActiveWorkspace workspace,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient(_handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, GoStatusUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", workspace.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Headers.TryAddWithoutValidation("x-org-id", workspace.OrganizationId);

        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new OpenCodeConsoleFetchResult(
                OpenCodeConsoleFetchOutcome.AuthenticationRequired,
                [],
                response.StatusCode);
        }

        TimeSpan? retryAfter = ProviderHttpSafety.GetRetryAfter(
            response,
            _timeProvider.GetUtcNow());
        if (retryAfter is not null)
        {
            return new OpenCodeConsoleFetchResult(
                OpenCodeConsoleFetchOutcome.RateLimited,
                [],
                response.StatusCode,
                retryAfter);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new OpenCodeConsoleFetchResult(
                OpenCodeConsoleFetchOutcome.TransientFailure,
                [],
                response.StatusCode);
        }

        try
        {
            using JsonDocument document = await ProviderHttpSafety
                .ReadJsonAsync(response, cancellationToken)
                .ConfigureAwait(false);
            if (!TryReadWindows(document.RootElement, out ImmutableArray<UsageWindow> windows))
            {
                return InvalidResponse(response.StatusCode);
            }

            return new OpenCodeConsoleFetchResult(
                OpenCodeConsoleFetchOutcome.Success,
                windows,
                response.StatusCode);
        }
        catch (InvalidDataException)
        {
            return InvalidResponse(response.StatusCode);
        }
    }

    private bool TryReadWindows(
        JsonElement root,
        out ImmutableArray<UsageWindow> windows)
    {
        windows = [];
        if (root.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("access", out JsonElement access))
        {
            return false;
        }

        if (access.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (access.ValueKind != JsonValueKind.Object ||
            !TryReadTimestamp(access, "endsAt", out DateTimeOffset monthReset) ||
            !access.TryGetProperty("meters", out JsonElement meters) ||
            meters.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var result = ImmutableArray.CreateBuilder<UsageWindow>(3);
        if (!TryAddWindow(meters, "fiveHour", "rolling", "resetsAt", null, true, result) ||
            !TryAddWindow(meters, "week", "weekly", "resetsAt", null, false, result) ||
            !TryAddWindow(meters, "month", "monthly", null, monthReset, false, result))
        {
            return false;
        }

        windows = result.ToImmutable();
        return true;
    }

    private bool TryAddWindow(
        JsonElement meters,
        string propertyName,
        string label,
        string? resetPropertyName,
        DateTimeOffset? fixedReset,
        bool allowNullReset,
        ImmutableArray<UsageWindow>.Builder windows)
    {
        if (!meters.TryGetProperty(propertyName, out JsonElement meter) ||
            meter.ValueKind != JsonValueKind.Object ||
            !TryReadAmount(meter, "limitMicroCents", out BigInteger limit) ||
            !TryReadAmount(meter, "usedMicroCents", out BigInteger used) ||
            limit <= BigInteger.Zero)
        {
            return false;
        }

        DateTimeOffset? reset;
        if (resetPropertyName is null)
        {
            reset = fixedReset;
        }
        else if (!TryReadOptionalTimestamp(meter, resetPropertyName, allowNullReset, out reset))
        {
            return false;
        }

        const int percentageScale = 1_000_000;
        BigInteger scaledPercent = used * (100 * percentageScale) / limit;
        double percent = (double)scaledPercent / percentageScale;
        if (!double.IsFinite(percent) || percent < 0)
        {
            return false;
        }

        windows.Add(new UsageWindow(label, percent, reset, _severityFromPercent(percent)));
        return true;
    }

    private static bool TryReadOptionalTimestamp(
        JsonElement parent,
        string propertyName,
        bool allowNull,
        out DateTimeOffset? timestamp)
    {
        timestamp = null;
        if (!parent.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return allowNull;
        }

        if (element.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                element.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            return false;
        }

        timestamp = parsed;
        return true;
    }

    private static bool TryReadTimestamp(
        JsonElement parent,
        string propertyName,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        return parent.TryGetProperty(propertyName, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
            element.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    private static bool TryReadAmount(
        JsonElement parent,
        string propertyName,
        out BigInteger value)
    {
        value = default;
        return parent.TryGetProperty(propertyName, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String &&
            BigInteger.TryParse(
            element.GetString(),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value) &&
            value >= BigInteger.Zero;
    }

    private static OpenCodeConsoleFetchResult InvalidResponse(HttpStatusCode? statusCode = null) => new(
        OpenCodeConsoleFetchOutcome.InvalidResponse,
        [],
        statusCode);
}
