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
                ProviderFetchResult usageResult = ParseUsage(document.RootElement, fetchedAt);
                if (usageResult.Snapshot?.Windows.Any(window => window.Label == "Monthly") != true)
                {
                    return usageResult;
                }

                JsonElement monthlyUsage = document.RootElement.GetProperty("limits").GetProperty("monthly")
                    .GetProperty("usage");
                decimal? monthlyFraction = monthlyUsage.TryGetDecimal(out decimal fraction) ? fraction : null;
                return await AddMonthlyBudgetAsync(client, identity, usageResult, monthlyFraction, cancellationToken)
                    .ConfigureAwait(false);
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

    private async Task<ProviderFetchResult> AddMonthlyBudgetAsync(HttpClient client, OllamaIdentity identity,
        ProviderFetchResult usageResult, decimal? monthlyFraction, CancellationToken cancellationToken)
    {
        ProviderSnapshot snapshot = usageResult.Snapshot!;
        try
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            using HttpRequestMessage request = identity.CreateAccountRequest(now);
            using HttpResponseMessage response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new ProviderFetchResult(ProviderFetchOutcome.AuthenticationRequired,
                    snapshot with { Health = HealthState.AuthExpired, Error = "re-auth: run ollama signin" },
                    response.StatusCode);
            }

            TimeSpan? retryAfter = ProviderHttpSafety.GetRetryAfter(response, now);
            if (retryAfter is not null)
            {
                return new ProviderFetchResult(ProviderFetchOutcome.RateLimited,
                    statusCode: response.StatusCode, retryAfter: retryAfter);
            }

            if (!response.IsSuccessStatusCode) return PlanUnavailable(snapshot, response.StatusCode);

            using JsonDocument document = await ProviderHttpSafety.ReadJsonAsync(response, cancellationToken)
                .ConfigureAwait(false);
            (string? label, decimal? budget) = ReadPlan(document.RootElement);
            snapshot = snapshot with { PlanLabel = label };
            if (budget is not decimal allowance)
            {
                return new ProviderFetchResult(usageResult.Outcome, snapshot);
            }

            decimal? estimatedSpend = EstimateSpend(monthlyFraction, allowance);
            snapshot = snapshot with
            {
                Info =
                [
                    new InfoLine("Estimated spend", estimatedSpend is decimal spend ? FormatUsd(spend) : "Unavailable"),
                    new InfoLine("Budget", FormatUsd(allowance)),
                ],
            };
            return estimatedSpend is null
                ? new ProviderFetchResult(ProviderFetchOutcome.PartialSuccess,
                    snapshot with { Health = HealthState.Degraded, Error = "Spend estimate is unavailable" })
                : new ProviderFetchResult(usageResult.Outcome, snapshot);
        }
        catch (HttpRequestException)
        {
            return PlanUnavailable(snapshot);
        }
        catch (InvalidDataException)
        {
            return PlanUnavailable(snapshot);
        }
        catch (IOException)
        {
            return PlanUnavailable(snapshot);
        }
    }

    private static (string? Label, decimal? Budget) ReadPlan(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid account metadata.");
        if (!root.TryGetProperty("Plan", out JsonElement plan) && !root.TryGetProperty("plan", out plan))
            return (null, null);
        if (plan.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid account plan.");

        // Published monthly included allowances, not subscription prices:
        // https://ollama.com/blog/transparent-pricing (2026-08-31).
        return plan.GetString()!.Trim().ToLowerInvariant() switch
        {
            "pro" => ("Pro", 60m),
            "max" => ("Max", 300m),
            "team" => ("Team", 1000m),
            "free" => ("Free", null),
            _ => (null, null),
        };
    }

    private static ProviderFetchResult PlanUnavailable(ProviderSnapshot snapshot, HttpStatusCode? statusCode = null) =>
        new(ProviderFetchOutcome.PartialSuccess,
            snapshot with { Health = HealthState.Degraded, Error = "Plan unavailable. Refresh to retry." }, statusCode);

    private static string FormatUsd(decimal value) => FormattableString.Invariant($"USD {value:0.00}");

    private static decimal? EstimateSpend(decimal? fraction, decimal allowance)
    {
        try
        {
            return fraction is decimal used
                ? decimal.Round(used * allowance, 2, MidpointRounding.AwayFromZero)
                : null;
        }
        catch (OverflowException)
        {
            return null;
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
