using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ReservePane.Model;

namespace ReservePane.Providers;

public sealed class OpenCodeCompanySeatProvider : IStatusProvider, IRetentionScopedStatusProvider, IProviderAvailability
{
    private static readonly BigInteger MicroCentsPerCent = new(1_000_000);
    private const int PercentageScale = 1_000_000;

    private readonly Func<double?, Severity> _severityFromPercent;
    private readonly TimeProvider _timeProvider;
    private readonly IOpenCodeConsoleActiveWorkspaceReader _workspaceReader;
    private readonly IOpenCodeCompanySeatClient _client;
    private readonly Func<string, bool> _commandAvailable;
    private readonly object _selectionGate = new();
    private ProviderRetentionScope _retentionScope = ProviderRetentionScope.Unknown;

    public OpenCodeCompanySeatProvider(
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider = null)
        : this(
            handler,
            severityFromPercent,
            timeProvider,
            new OpenCodeConsoleActiveWorkspaceReader(),
            new OpenCodeCompanySeatClient(handler, timeProvider))
    {
    }

    internal OpenCodeCompanySeatProvider(
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider,
        IOpenCodeConsoleActiveWorkspaceReader workspaceReader,
        IOpenCodeCompanySeatClient client)
        : this(handler, severityFromPercent, timeProvider, workspaceReader, client, CommandAvailability.IsAvailable)
    {
    }

    internal OpenCodeCompanySeatProvider(
        HttpMessageHandler handler,
        Func<double?, Severity> severityFromPercent,
        TimeProvider? timeProvider,
        IOpenCodeConsoleActiveWorkspaceReader workspaceReader,
        IOpenCodeCompanySeatClient client,
        Func<string, bool> commandAvailable)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _severityFromPercent = severityFromPercent;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _workspaceReader = workspaceReader;
        _client = client;
        _commandAvailable = commandAvailable;
    }

    public string Id => "opencode-company-seat";

    public string Label => "OpenCode";

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_commandAvailable("opencode"));
    }

    ProviderRetentionScope IRetentionScopedStatusProvider.RetentionScope
    {
        get
        {
            lock (_selectionGate)
            {
                return _retentionScope;
            }
        }
    }

    public async Task<ProviderFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset fetchedAt = _timeProvider.GetUtcNow();
        OpenCodeConsoleActiveWorkspaceReadResult workspaceResult = await ReadWorkspaceAsync(
            cancellationToken).ConfigureAwait(false);
        if (workspaceResult.Outcome != OpenCodeConsoleActiveWorkspaceReadOutcome.Success)
        {
            return new ProviderFetchResult(
                workspaceResult.Outcome == OpenCodeConsoleActiveWorkspaceReadOutcome.InvalidResponse
                    ? ProviderFetchOutcome.InvalidResponse
                    : ProviderFetchOutcome.TransientFailure,
                preserveLastGoodData: false);
        }

        OpenCodeConsoleActiveWorkspace? workspace = workspaceResult.Workspace;
        if (workspace is null)
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.NotConfigured,
                Snapshot(
                    HealthState.Unreachable,
                    [],
                    [],
                    "OpenCode Console active workspace is not configured",
                    fetchedAt));
        }

        if (workspace.ExpiresAt is DateTimeOffset expiresAt && expiresAt <= fetchedAt)
        {
            return AuthenticationRequired(fetchedAt);
        }

        OpenCodeCompanySeatFetchResult result = await _client
            .FetchAsync(workspace, cancellationToken)
            .ConfigureAwait(false);
        if (result.Outcome != OpenCodeCompanySeatFetchOutcome.Success)
        {
            return MapFailure(result, fetchedAt);
        }

        return BuildSuccess(result.Budget, fetchedAt);
    }

    async Task<ProviderRetentionScopeRefreshOutcome> IRetentionScopedStatusProvider
        .RefreshRetentionScopeAsync(CancellationToken cancellationToken)
    {
        OpenCodeConsoleActiveWorkspaceReadResult result = await ReadWorkspaceAsync(cancellationToken)
            .ConfigureAwait(false);
        return result.Outcome switch
        {
            OpenCodeConsoleActiveWorkspaceReadOutcome.Success =>
                ProviderRetentionScopeRefreshOutcome.Success,
            OpenCodeConsoleActiveWorkspaceReadOutcome.TransientFailure =>
                ProviderRetentionScopeRefreshOutcome.TransientFailure,
            _ => ProviderRetentionScopeRefreshOutcome.InvalidResponse,
        };
    }

    private ProviderFetchResult BuildSuccess(
        OpenCodeCompanySeatBudget? budget,
        DateTimeOffset fetchedAt)
    {
        if (budget is null)
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.PartialSuccess,
                Snapshot(
                    HealthState.Degraded,
                    [],
                    [new InfoLine("Budget", "Not configured")],
                    "No effective member budget is configured",
                    fetchedAt));
        }

        ImmutableArray<InfoLine> info =
        [
            new InfoLine("Spend", FormatUsd(budget.SpentMicroCents)),
            new InfoLine(
                "Budget",
                budget.LimitMicroCents is BigInteger limit
                    ? FormatUsd(limit)
                    : "Not configured"),
        ];

        double? percent = null;
        Severity severity;
        HealthState health = HealthState.Ok;
        string? error = null;
        if (budget.LimitMicroCents is null)
        {
            severity = Severity.Normal;
            health = HealthState.Degraded;
            error = "No effective member budget is configured";
        }
        else if (budget.LimitMicroCents == BigInteger.Zero)
        {
            severity = budget.Exceeded ? Severity.Critical : Severity.Normal;
            if (budget.Exceeded)
            {
                health = HealthState.Degraded;
                error = "Monthly budget exceeded";
            }
        }
        else if (!TryCalculatePercent(
            budget.SpentMicroCents,
            budget.LimitMicroCents.Value,
            out double calculated))
        {
            return new ProviderFetchResult(
                ProviderFetchOutcome.InvalidResponse);
        }
        else
        {
            percent = calculated;
            severity = _severityFromPercent(percent);
        }

        ImmutableArray<UsageWindow> windows =
        [new UsageWindow("monthly budget", percent, budget.ResetsAt, severity)];
        ProviderFetchOutcome outcome = budget.LimitMicroCents is null
            ? ProviderFetchOutcome.PartialSuccess
            : ProviderFetchOutcome.Success;
        return new ProviderFetchResult(
            outcome,
            Snapshot(health, windows, info, error, fetchedAt));
    }

    private static ProviderFetchResult MapFailure(
        OpenCodeCompanySeatFetchResult result,
        DateTimeOffset fetchedAt) => result.Outcome switch
        {
            OpenCodeCompanySeatFetchOutcome.AuthenticationRequired => AuthenticationRequired(
                fetchedAt,
                result.StatusCode),
            OpenCodeCompanySeatFetchOutcome.RateLimited => new ProviderFetchResult(
                ProviderFetchOutcome.RateLimited,
                statusCode: result.StatusCode,
                retryAfter: result.RetryAfter),
            OpenCodeCompanySeatFetchOutcome.InvalidResponse => new ProviderFetchResult(
                ProviderFetchOutcome.InvalidResponse,
                statusCode: result.StatusCode),
            _ => new ProviderFetchResult(
                ProviderFetchOutcome.TransientFailure,
                statusCode: result.StatusCode),
        };

    private static ProviderFetchResult AuthenticationRequired(
        DateTimeOffset fetchedAt,
        System.Net.HttpStatusCode? statusCode = null) => new(
        ProviderFetchOutcome.AuthenticationRequired,
        Snapshot(
            HealthState.AuthExpired,
            [],
            [],
            "re-auth: run opencode console login",
            fetchedAt),
        statusCode);

    private static bool TryCalculatePercent(
        BigInteger spent,
        BigInteger limit,
        out double percent)
    {
        BigInteger scaled = spent * (100 * PercentageScale) / limit;
        percent = (double)scaled / PercentageScale;
        return double.IsFinite(percent) && percent >= 0;
    }

    private static string FormatUsd(BigInteger microCents)
    {
        BigInteger cents = BigInteger.DivRem(
            microCents,
            MicroCentsPerCent,
            out BigInteger remainder);
        if (remainder * 2 >= MicroCentsPerCent)
        {
            cents += BigInteger.One;
        }

        BigInteger dollars = BigInteger.DivRem(cents, 100, out BigInteger centRemainder);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"USD {dollars}.{centRemainder:D2}");
    }

    private async Task<OpenCodeConsoleActiveWorkspaceReadResult> ReadWorkspaceAsync(
        CancellationToken cancellationToken)
    {
        SetRetentionScope(ProviderRetentionScope.Unknown);
        OpenCodeConsoleActiveWorkspaceReadResult result = await _workspaceReader
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        ProviderRetentionScope scope = result.Outcome == OpenCodeConsoleActiveWorkspaceReadOutcome.Success
            ? ProviderRetentionScope.Known(CreateSelectionKey(result.Workspace))
            : ProviderRetentionScope.Unknown;
        SetRetentionScope(scope);
        return result;
    }

    private void SetRetentionScope(ProviderRetentionScope scope)
    {
        lock (_selectionGate)
        {
            _retentionScope = scope;
        }
    }

    private static string? CreateSelectionKey(OpenCodeConsoleActiveWorkspace? workspace) =>
        workspace is null
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                workspace.AccountId + "\0" + workspace.OrganizationId)));

    private static ProviderSnapshot Snapshot(
        HealthState health,
        ImmutableArray<UsageWindow> windows,
        ImmutableArray<InfoLine> info,
        string? error,
        DateTimeOffset fetchedAt) => new(
        "opencode-company-seat",
        "OpenCode",
        health,
        "Company Seat",
        windows,
        info,
        error,
        fetchedAt,
        0);
}
