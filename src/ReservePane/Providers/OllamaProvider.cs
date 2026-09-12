using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Renci.SshNet.Common;
using ReservePane.Model;

namespace ReservePane.Providers;

public sealed class OllamaProvider : IStatusProvider, IProviderAvailability, IRetentionScopedStatusProvider
{
    private readonly string _privateKeyPath;
    private readonly HttpMessageHandler _handler;
    private readonly Func<double?, Severity> _severityFromPercent;
    private readonly TimeProvider _timeProvider;
    private readonly object _scopeGate = new();
    private ProviderRetentionScope _retentionScope = ProviderRetentionScope.Unknown;

    public OllamaProvider(string privateKeyPath, HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent, TimeProvider? timeProvider = null)
    {
        _privateKeyPath = privateKeyPath;
        _handler = handler;
        _severityFromPercent = severityFromPercent;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Id => "ollama";
    public string Label => "Ollama Cloud";

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        CredentialFilePrerequisite.IsPresentOrIndeterminateAsync(
            _privateKeyPath, CredentialFilePrerequisite.Probe, cancellationToken);

    ProviderRetentionScope IRetentionScopedStatusProvider.RetentionScope
    {
        get { lock (_scopeGate) { return _retentionScope; } }
    }

    Task<ProviderRetentionScopeRefreshOutcome> IRetentionScopedStatusProvider.RefreshRetentionScopeAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using OllamaIdentity identity = ReadIdentity();
            return Task.FromResult(ProviderRetentionScopeRefreshOutcome.Success);
        }
        catch (Exception exception) when (IsMissingIdentity(exception))
        {
            SetRetentionScope(ProviderRetentionScope.Known(null));
            return Task.FromResult(ProviderRetentionScopeRefreshOutcome.Success);
        }
        catch (Exception exception) when (IsInvalidIdentity(exception))
        {
            return Task.FromResult(ProviderRetentionScopeRefreshOutcome.InvalidResponse);
        }
    }

    public async Task<ProviderFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset fetchedAt = _timeProvider.GetUtcNow();
        OllamaIdentity identity;
        try
        {
            identity = ReadIdentity();
        }
        catch (Exception exception) when (IsMissingIdentity(exception))
        {
            SetRetentionScope(ProviderRetentionScope.Known(null));
            return new ProviderFetchResult(ProviderFetchOutcome.NotConfigured,
                Snapshot(HealthState.Unreachable, [], "sign in: run ollama signin", fetchedAt));
        }
        catch (Exception exception) when (IsInvalidIdentity(exception))
        {
            return AuthenticationRequired(fetchedAt);
        }

        using (identity)
        {
            try
            {
                using var client = new HttpClient(_handler, disposeHandler: false);
                using HttpRequestMessage request = identity.CreateUsageRequest(fetchedAt);
                using HttpResponseMessage response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return AuthenticationRequired(fetchedAt, response.StatusCode);
                }

                TimeSpan? retryAfter = ProviderHttpSafety.GetRetryAfter(response, fetchedAt);
                if (retryAfter is not null)
                {
                    return new ProviderFetchResult(ProviderFetchOutcome.RateLimited,
                        statusCode: response.StatusCode, retryAfter: retryAfter);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new ProviderFetchResult(ProviderFetchOutcome.TransientFailure, statusCode: response.StatusCode);
                }

                using JsonDocument document = await ProviderHttpSafety.ReadJsonAsync(response, cancellationToken)
                    .ConfigureAwait(false);
                return ParseUsage(document.RootElement, fetchedAt);
            }
            catch (HttpRequestException)
            {
                return new ProviderFetchResult(ProviderFetchOutcome.TransientFailure);
            }
            catch (InvalidDataException)
            {
                return new ProviderFetchResult(ProviderFetchOutcome.InvalidResponse);
            }
        }
    }

    private ProviderFetchResult ParseUsage(JsonElement root, DateTimeOffset fetchedAt)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("limits", out JsonElement limits) || limits.ValueKind != JsonValueKind.Object)
        {
            return new ProviderFetchResult(ProviderFetchOutcome.InvalidResponse);
        }

        var windows = ImmutableArray.CreateBuilder<UsageWindow>();
        string? unavailableReason = null;
        foreach ((string name, string label) in new[] { ("session", "Session"), ("weekly", "Weekly"), ("monthly", "Monthly") })
        {
            if (!limits.TryGetProperty(name, out JsonElement window)) continue;
            if (window.ValueKind != JsonValueKind.Object ||
                !window.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Number ||
                !usage.TryGetDouble(out double value) || !double.IsFinite(value * 100) || value < 0 ||
                (name != "monthly" && value > 1))
            {
                unavailableReason = "Some usage limits are unavailable";
                continue;
            }

            // Limit usage is a consumed fraction; activity costs cover a separate reporting period.
            double percent = value * 100;
            windows.Add(new UsageWindow(label, percent, null, _severityFromPercent(percent)));
        }

        if (windows.Count == 0) return new ProviderFetchResult(ProviderFetchOutcome.InvalidResponse);
        bool partial = unavailableReason is not null;
        return new ProviderFetchResult(partial ? ProviderFetchOutcome.PartialSuccess : ProviderFetchOutcome.Success,
            Snapshot(partial ? HealthState.Degraded : HealthState.Ok, windows.ToImmutable(),
                unavailableReason, fetchedAt));
    }

    private OllamaIdentity ReadIdentity()
    {
        SetRetentionScope(ProviderRetentionScope.Unknown);
        OllamaIdentity identity = OllamaIdentity.Read(_privateKeyPath);
        SetRetentionScope(ProviderRetentionScope.Known(identity.RetentionKey));
        return identity;
    }

    private void SetRetentionScope(ProviderRetentionScope scope)
    {
        lock (_scopeGate) { _retentionScope = scope; }
    }

    private ProviderFetchResult AuthenticationRequired(DateTimeOffset fetchedAt, HttpStatusCode? statusCode = null) =>
        new(ProviderFetchOutcome.AuthenticationRequired,
            Snapshot(HealthState.AuthExpired, [], "re-auth: run ollama signin", fetchedAt), statusCode);

    private ProviderSnapshot Snapshot(HealthState health, ImmutableArray<UsageWindow> windows, string? error,
        DateTimeOffset fetchedAt) => new(Id, Label, health, null, windows, [], error, fetchedAt, 0);

    private static bool IsMissingIdentity(Exception exception) => exception is FileNotFoundException or DirectoryNotFoundException;
    private static bool IsInvalidIdentity(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or
        SecurityException or SshException or ArgumentException or FormatException or CryptographicException or NotSupportedException;
}
