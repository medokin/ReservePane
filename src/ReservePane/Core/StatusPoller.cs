using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Channels;
using ReservePane.Model;
using ReservePane.Providers;

namespace ReservePane.Core;

public sealed class StatusPoller : IActivityCadencePoller
{
    private static readonly TimeSpan DefaultProviderTimeout = TimeSpan.FromSeconds(10);
    private readonly IReadOnlyList<IStatusProvider> _providers;
    private readonly Func<AppSettings> _settings;
    private readonly RollingFileLog _log;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _providerTimeout;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly object _cadenceGate = new();
    private readonly object _notificationGate = new();
    private readonly Queue<Action> _notificationQueue = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProviderRetentionScope> _retentionScopes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AvailabilityProbe> _availabilityProbes = new(StringComparer.Ordinal);
    private readonly Channel<bool> _refreshRequests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    private StatusReport _current;
    private Task _notificationPump = Task.CompletedTask;
    private bool _notificationPumpRunning;
    private int _reducedCadence;
    private int _runActive;
    private int _refreshing;

    public StatusPoller(
        IReadOnlyList<IStatusProvider> providers,
        Func<AppSettings> settings,
        RollingFileLog log,
        TimeProvider? timeProvider = null,
        TimeSpan? providerTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        _providers = providers;
        _settings = settings;
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _providerTimeout = providerTimeout ?? DefaultProviderTimeout;
        _synchronizationContext = SynchronizationContext.Current;
        _current = StatusReport.Empty(_timeProvider.GetUtcNow());
    }

    public StatusReport Current => Volatile.Read(ref _current);

    public bool IsRefreshing => Volatile.Read(ref _refreshing) != 0;

    public event EventHandler<bool>? RefreshStateChanged;

    public event EventHandler<StatusReport>? ReportUpdated;

    public async Task<StatusReport> PollOnceAsync(CancellationToken cancellationToken)
    {
        StatusReport report;
        TaskCompletionSource? notificationPump;
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetRefreshing(true);
            StatusReport previous = Current;
            Dictionary<string, ProviderSnapshot> previousById = previous.Providers
                .GroupBy(snapshot => snapshot.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

            Task<ProviderAttempt>[] fetches = _providers
                .Select(provider => PrepareProviderAttemptAsync(
                    provider,
                    previousById.GetValueOrDefault(provider.Id),
                    cancellationToken))
                .ToArray();

            ProviderAttempt[] attempts = await Task.WhenAll(fetches).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (ProviderAttempt hidden in attempts.Where(ShouldOmit))
            {
                CommitOmitted(hidden);
            }

            ProviderSnapshot[] providers = attempts
                .Where(attempt => !ShouldOmit(attempt))
                .Select(ApplyAttempt)
                .ToArray();

            report = new StatusReport(_timeProvider.GetUtcNow(), providers.ToImmutableArray());
            Volatile.Write(ref _current, report);
            notificationPump = EnqueueReportUpdated(report);
        }
        finally
        {
            SetRefreshing(false);
            _pollGate.Release();
        }

        if (notificationPump is not null)
        {
            StartNotificationPump(notificationPump);
        }

        return report;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await Start(cancellationToken).Completion.ConfigureAwait(false);
    }

    internal PollLoopRun Start(CancellationToken cancellationToken)
    {
        lock (_cadenceGate)
        {
            if (_runActive != 0)
            {
                throw new InvalidOperationException("The status poller is already running.");
            }

            Volatile.Write(ref _runActive, 1);
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task completion = RunAsyncCore(cancellationToken, ready);
        return new PollLoopRun(
            completion,
            WaitForReadinessAsync(ready.Task, completion, cancellationToken));
    }

    PollLoopRun IActivityCadencePoller.Start(CancellationToken cancellationToken) =>
        Start(cancellationToken);

    private async Task RunAsyncCore(
        CancellationToken cancellationToken,
        TaskCompletionSource ready)
    {
        try
        {
            // The loop owns cancellation so it always reaches notification drain and cleanup.
            await Task.Run(
                () => RunLoopAsync(cancellationToken, ready),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_cadenceGate)
            {
                Volatile.Write(ref _runActive, 0);
            }
        }
    }

    private async Task RunLoopAsync(
        CancellationToken cancellationToken,
        TaskCompletionSource ready)
    {
        PeriodicTimer? timer = null;
        try
        {
            TimeSpan cadence = GetCadence();
            timer = new PeriodicTimer(cadence, _timeProvider);
            ready.TrySetResult();

            while (true)
            {
                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task<bool> tick = timer.WaitForNextTickAsync(waitCancellation.Token).AsTask();
                Task refresh = WaitForRefreshAsync(waitCancellation.Token);
                Task completed = await Task.WhenAny(tick, refresh).ConfigureAwait(false);

                waitCancellation.Cancel();
                await Task.WhenAll(
                    ObserveCancellationAsync(tick),
                    ObserveCancellationAsync(refresh)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (completed == tick && !await tick.ConfigureAwait(false))
                {
                    break;
                }

                await PollOnceAsync(cancellationToken).ConfigureAwait(false);

                TimeSpan nextCadence = GetCadence();
                if (nextCadence != cadence)
                {
                    timer.Dispose();
                    cadence = nextCadence;
                    timer = new PeriodicTimer(cadence, _timeProvider);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            timer?.Dispose();
            await GetNotificationPump().ConfigureAwait(false);
        }
    }

    private static async Task WaitForReadinessAsync(
        Task ready,
        Task completion,
        CancellationToken cancellationToken)
    {
        Task completed = await Task.WhenAny(ready, completion)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (completed == ready)
        {
            return;
        }

        await completion.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("The status poller stopped before initialization completed.");
    }

    public void RequestRefresh()
    {
        _refreshRequests.Writer.TryWrite(true);
    }

    public void SetReducedCadence(bool reduced)
    {
        int value = reduced ? 1 : 0;
        lock (_cadenceGate)
        {
            if (Interlocked.Exchange(ref _reducedCadence, value) != value && _runActive != 0)
            {
                RequestRefresh();
            }
        }
    }

    private async Task<ProviderAttempt> PrepareProviderAttemptAsync(
        IStatusProvider provider,
        ProviderSnapshot? previous,
        CancellationToken cancellationToken)
    {
        ProviderAttempt? unavailable = await CheckAvailabilityAsync(
            provider,
            previous,
            cancellationToken).ConfigureAwait(false);
        if (unavailable is not null)
        {
            return unavailable;
        }

        if (_cooldowns.TryGetValue(provider.Id, out DateTimeOffset cooldownUntil) &&
            cooldownUntil > _timeProvider.GetUtcNow())
        {
            if (provider is IRetentionScopedStatusProvider scopedProvider)
            {
                ProviderAttempt? refreshFailure = await RefreshRetentionScopeAsync(
                    provider,
                    scopedProvider,
                    previous,
                    cancellationToken).ConfigureAwait(false);
                if (refreshFailure is not null)
                {
                    return refreshFailure;
                }

                if (!RetentionScopeMatches(provider))
                {
                    return new ProviderAttempt(
                        provider,
                        previous,
                        ProviderAttemptKind.Completed,
                        new ProviderFetchResult(
                            ProviderFetchOutcome.TransientFailure,
                            preserveLastGoodData: false),
                        PreserveCooldown: true);
                }
            }

            return new ProviderAttempt(provider, previous, ProviderAttemptKind.CoolingDown);
        }

        return await FetchProviderAsync(provider, previous, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderAttempt?> CheckAvailabilityAsync(
        IStatusProvider provider,
        ProviderSnapshot? previous,
        CancellationToken cancellationToken)
    {
        if (provider is not IProviderAvailability availability)
        {
            return null;
        }

        AvailabilityProbe availabilityProbe = GetAvailabilityProbe(provider, availability);
        if (availabilityProbe.Cancellation.IsCancellationRequested)
        {
            return new ProviderAttempt(
                provider,
                previous,
                ProviderAttemptKind.AvailabilityTimedOut,
                Exception: new TaskCanceledException());
        }

        using var timeout = new CancellationTokenSource(_providerTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            bool isAvailable = await availabilityProbe.Task
                .WaitAsync(linked.Token)
                .ConfigureAwait(false);
            RemoveAvailabilityProbe(provider.Id, availabilityProbe);
            return isAvailable
                ? null
                : new ProviderAttempt(provider, previous, ProviderAttemptKind.Unavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelAvailabilityProbe(availabilityProbe);
            throw;
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            CancelAvailabilityProbe(availabilityProbe);
            return new ProviderAttempt(
                provider,
                previous,
                ProviderAttemptKind.AvailabilityTimedOut,
                Exception: exception);
        }
        catch (OperationCanceledException exception) when (
            availabilityProbe.Cancellation.IsCancellationRequested)
        {
            return new ProviderAttempt(
                provider,
                previous,
                ProviderAttemptKind.AvailabilityTimedOut,
                Exception: exception);
        }
        catch (Exception exception)
        {
            RemoveAvailabilityProbe(provider.Id, availabilityProbe);
            return new ProviderAttempt(
                provider,
                previous,
                ProviderAttemptKind.AvailabilityFailed,
                Exception: exception);
        }
    }

    private AvailabilityProbe GetAvailabilityProbe(
        IStatusProvider provider,
        IProviderAvailability availability)
    {
        while (true)
        {
            if (_availabilityProbes.TryGetValue(provider.Id, out AvailabilityProbe? existing))
            {
                if (existing.Task.IsCompleted)
                {
                    RemoveAvailabilityProbe(provider.Id, existing);
                    continue;
                }

                return existing;
            }

            var cancellation = new CancellationTokenSource();
            Task<bool> task = Task.Run(
                () => availability.IsAvailableAsync(cancellation.Token),
                CancellationToken.None);
            var created = new AvailabilityProbe(task, cancellation);
            if (_availabilityProbes.TryAdd(provider.Id, created))
            {
                _ = ObserveAvailabilityProbeAsync(provider.Id, created);
                return created;
            }

            CancelAvailabilityProbe(created);
            _ = DisposeAvailabilityProbeAsync(created);
        }
    }

    private async Task ObserveAvailabilityProbeAsync(
        string providerId,
        AvailabilityProbe probe)
    {
        try
        {
            await probe.Task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            RemoveAvailabilityProbe(providerId, probe);
            probe.Cancellation.Dispose();
        }
    }

    private static async Task DisposeAvailabilityProbeAsync(AvailabilityProbe probe)
    {
        try
        {
            await probe.Task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            probe.Cancellation.Dispose();
        }
    }

    private void RemoveAvailabilityProbe(string providerId, AvailabilityProbe probe) =>
        _availabilityProbes.TryRemove(
            new KeyValuePair<string, AvailabilityProbe>(providerId, probe));

    private static void CancelAvailabilityProbe(AvailabilityProbe probe)
    {
        try
        {
            probe.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static bool IsUnavailable(ProviderAttempt attempt) => attempt.Kind is
        ProviderAttemptKind.Unavailable or
        ProviderAttemptKind.AvailabilityTimedOut or
        ProviderAttemptKind.AvailabilityFailed;

    private static bool ShouldOmit(ProviderAttempt attempt) =>
        IsUnavailable(attempt) ||
        attempt.Result?.Outcome == ProviderFetchOutcome.NotConfigured;

    private void CommitOmitted(ProviderAttempt attempt)
    {
        _cooldowns.TryRemove(attempt.Provider.Id, out _);
        _retentionScopes.TryRemove(attempt.Provider.Id, out _);

        if (attempt.Result is ProviderFetchResult result)
        {
            LogProviderResult(
                attempt.Provider,
                result,
                result.Snapshot! with { ConsecutiveFailures = 0 });
            return;
        }

        _log.Write(
            LogArea.Provider,
            attempt.Kind switch
            {
                ProviderAttemptKind.Unavailable => LogOutcome.Unreachable,
                ProviderAttemptKind.AvailabilityTimedOut => LogOutcome.TimedOut,
                ProviderAttemptKind.AvailabilityFailed => LogOutcome.Failed,
                _ => throw new InvalidOperationException("Validated availability outcome was not mapped."),
            },
            exception: attempt.Exception,
            providerId: attempt.Provider.Id);
    }

    private async Task<ProviderAttempt?> RefreshRetentionScopeAsync(
        IStatusProvider provider,
        IRetentionScopedStatusProvider scopedProvider,
        ProviderSnapshot? previous,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_providerTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            ProviderRetentionScopeRefreshOutcome outcome = await scopedProvider
                .RefreshRetentionScopeAsync(linked.Token)
                .ConfigureAwait(false);
            return outcome switch
            {
                ProviderRetentionScopeRefreshOutcome.Success => null,
                ProviderRetentionScopeRefreshOutcome.InvalidResponse => new ProviderAttempt(
                    provider,
                    previous,
                    ProviderAttemptKind.Completed,
                    new ProviderFetchResult(
                        ProviderFetchOutcome.InvalidResponse,
                        preserveLastGoodData: false),
                    PreserveCooldown: true),
                _ => new ProviderAttempt(
                    provider,
                    previous,
                    ProviderAttemptKind.Completed,
                    new ProviderFetchResult(
                        ProviderFetchOutcome.TransientFailure,
                        preserveLastGoodData: false),
                    PreserveCooldown: true),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            return new ProviderAttempt(
                provider,
                previous,
                ProviderAttemptKind.TimedOut,
                new ProviderFetchResult(ProviderFetchOutcome.TransientFailure),
                exception,
                PreserveCooldown: true);
        }
        catch (Exception exception)
        {
            return new ProviderAttempt(
                provider,
                previous,
                ProviderAttemptKind.Failed,
                new ProviderFetchResult(ProviderFetchOutcome.TransientFailure),
                exception,
                PreserveCooldown: true);
        }
    }

    private async Task<ProviderAttempt> FetchProviderAsync(
        IStatusProvider provider,
        ProviderSnapshot? previous,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_providerTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            ProviderFetchResult result = await provider.FetchAsync(linked.Token).ConfigureAwait(false);
            return new ProviderAttempt(provider, previous, ProviderAttemptKind.Completed, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            return new ProviderAttempt(
                provider,
                previous,
                ProviderAttemptKind.TimedOut,
                new ProviderFetchResult(ProviderFetchOutcome.TransientFailure),
                exception);
        }
        catch (Exception exception)
        {
            return new ProviderAttempt(
                provider,
                previous,
                ProviderAttemptKind.Failed,
                new ProviderFetchResult(ProviderFetchOutcome.TransientFailure),
                exception);
        }
    }

    private ProviderSnapshot ApplyAttempt(ProviderAttempt attempt)
    {
        if (attempt.Kind == ProviderAttemptKind.CoolingDown)
        {
            return RetainCooldown(attempt.Provider, attempt.Previous);
        }

        if (!attempt.PreserveCooldown)
        {
            if (attempt.Provider.MinimumRefreshInterval > TimeSpan.Zero)
            {
                _cooldowns[attempt.Provider.Id] = _timeProvider.GetUtcNow() + attempt.Provider.MinimumRefreshInterval;
            }
            else
            {
                _cooldowns.TryRemove(attempt.Provider.Id, out _);
            }
        }

        if (attempt.Kind is ProviderAttemptKind.TimedOut or ProviderAttemptKind.Failed)
        {
            ProviderSnapshot retained = RetainScopedFailure(
                attempt.Provider,
                attempt.Previous,
                preserveLastGoodData: true) with
            {
                Error = attempt.Kind == ProviderAttemptKind.TimedOut
                    ? "Provider request timed out. Refresh to retry."
                    : attempt.Exception is HttpRequestException
                        ? "Connection failed. Refresh to retry."
                        : "Provider refresh failed. Refresh to retry.",
            };
            _log.Write(
                LogArea.Provider,
                attempt.Kind == ProviderAttemptKind.TimedOut ? LogOutcome.TimedOut : LogOutcome.Failed,
                exception: attempt.Exception,
                providerId: attempt.Provider.Id,
                providerOutcome: ProviderFetchOutcome.TransientFailure,
                consecutiveFailures: retained.ConsecutiveFailures);
            return retained;
        }

        return ApplyResult(
            attempt.Provider,
            attempt.Previous,
            attempt.Result!);
    }

    private ProviderSnapshot ApplyResult(
        IStatusProvider provider,
        ProviderSnapshot? previous,
        ProviderFetchResult result)
    {
        ProviderSnapshot? snapshot = result.Snapshot;
        if (snapshot is not null && !string.Equals(snapshot.Id, provider.Id, StringComparison.Ordinal))
        {
            ProviderSnapshot retained = RetainScopedFailure(
                provider,
                previous,
                preserveLastGoodData: true) with { Error = "Provider returned an unexpected response." };
            LogProviderResult(
                provider,
                new ProviderFetchResult(ProviderFetchOutcome.InvalidResponse, statusCode: result.StatusCode),
                retained);
            return retained;
        }

        if (result.Outcome == ProviderFetchOutcome.RateLimited)
        {
            TimeSpan retryAfter = result.RetryAfter!.Value;
            if (retryAfter < provider.MinimumRefreshInterval)
            {
                retryAfter = provider.MinimumRefreshInterval;
            }
            DateTimeOffset cooldownUntil = _timeProvider.GetUtcNow() + retryAfter;
            _cooldowns[provider.Id] = cooldownUntil;
            ProviderSnapshot retained = RetainScopedFailure(
                provider,
                previous,
                result.PreserveLastGoodData) with
            {
                Error = FormattableString.Invariant($"Rate limited. Retry after {cooldownUntil.ToLocalTime():HH:mm:ss}."),
            };
            LogProviderResult(provider, result, retained, retryAfter);
            return retained;
        }

        switch (result.Outcome)
        {
            case ProviderFetchOutcome.Success:
            case ProviderFetchOutcome.PartialSuccess:
            case ProviderFetchOutcome.NotConfigured:
            case ProviderFetchOutcome.AuthenticationRequired:
                ProviderSnapshot published = snapshot! with { ConsecutiveFailures = 0 };
                RecordRetentionScope(provider);
                LogProviderResult(provider, result, published);
                return published;
            case ProviderFetchOutcome.TransientFailure:
                ProviderSnapshot transient = RetainScopedFailure(
                    provider,
                    previous,
                    result.PreserveLastGoodData) with
                {
                    Error = result.StatusCode is { } status
                        ? FormattableString.Invariant($"Provider request failed (HTTP {(int)status}). Refresh to retry.")
                        : "Provider refresh failed. Refresh to retry.",
                };
                LogProviderResult(provider, result, transient);
                return transient;
            case ProviderFetchOutcome.InvalidResponse:
                ProviderSnapshot invalid = RetainScopedFailure(
                    provider,
                    previous,
                    result.PreserveLastGoodData) with { Error = "Provider returned an unexpected response." };
                LogProviderResult(provider, result, invalid);
                return invalid;
            default:
                throw new InvalidOperationException("Validated provider outcome was not mapped.");
        }
    }

    private void LogProviderResult(
        IStatusProvider provider,
        ProviderFetchResult result,
        ProviderSnapshot snapshot,
        TimeSpan? effectiveRetryAfter = null)
    {
        LogOutcome logOutcome = result.Outcome switch
        {
            ProviderFetchOutcome.Success => LogOutcome.Succeeded,
            ProviderFetchOutcome.PartialSuccess => LogOutcome.Degraded,
            ProviderFetchOutcome.NotConfigured => LogOutcome.Unreachable,
            ProviderFetchOutcome.AuthenticationRequired => LogOutcome.AuthExpired,
            ProviderFetchOutcome.TransientFailure => LogOutcome.Failed,
            ProviderFetchOutcome.RateLimited => LogOutcome.Failed,
            ProviderFetchOutcome.InvalidResponse => LogOutcome.Invalid,
            _ => throw new InvalidOperationException("Validated provider outcome was not mapped."),
        };
        int? cooldownSeconds = (effectiveRetryAfter ?? result.RetryAfter) is TimeSpan retryAfter
            ? checked((int)Math.Ceiling(retryAfter.TotalSeconds))
            : null;
        _log.Write(
            LogArea.Provider,
            logOutcome,
            statusCode: (int?)result.StatusCode,
            providerId: provider.Id,
            providerOutcome: result.Outcome,
            cooldownSeconds: cooldownSeconds,
            consecutiveFailures: snapshot.ConsecutiveFailures);
    }

    private ProviderSnapshot RetainCooldown(IStatusProvider provider, ProviderSnapshot? previous) =>
        previous ?? new ProviderSnapshot(
            provider.Id,
            provider.Label,
            HealthState.Unreachable,
            null,
            ImmutableArray<UsageWindow>.Empty,
            ImmutableArray<InfoLine>.Empty,
            null,
            _timeProvider.GetUtcNow(),
            1);

    private ProviderSnapshot RetainScopedFailure(
        IStatusProvider provider,
        ProviderSnapshot? previous,
        bool preserveLastGoodData)
    {
        bool canRetain = preserveLastGoodData && RetentionScopeMatches(provider);
        if (!canRetain)
        {
            RecordRetentionScope(provider);
        }

        return RetainFailure(provider, canRetain ? previous : null);
    }

    private bool RetentionScopeMatches(IStatusProvider provider)
    {
        if (provider is not IRetentionScopedStatusProvider scopedProvider)
        {
            return true;
        }

        return _retentionScopes.TryGetValue(provider.Id, out ProviderRetentionScope previousScope) &&
            previousScope.IsKnown &&
            scopedProvider.RetentionScope.IsKnown &&
            previousScope == scopedProvider.RetentionScope;
    }

    private void RecordRetentionScope(IStatusProvider provider)
    {
        if (provider is IRetentionScopedStatusProvider scopedProvider)
        {
            _retentionScopes[provider.Id] = scopedProvider.RetentionScope;
        }
    }

    private ProviderSnapshot RetainFailure(IStatusProvider provider, ProviderSnapshot? previous)
    {
        int failures = checked((previous?.ConsecutiveFailures ?? 0) + 1);
        HealthState health = failures >= 3
            ? HealthState.Degraded
            : previous?.Health ?? HealthState.Unreachable;

        if (failures >= 3)
        {
            _log.Write(LogArea.Poller, LogOutcome.Degraded);
        }

        return previous is null
            ? new ProviderSnapshot(
                provider.Id,
                provider.Label,
                health,
                null,
                ImmutableArray<UsageWindow>.Empty,
                ImmutableArray<InfoLine>.Empty,
                null,
                _timeProvider.GetUtcNow(),
                failures)
            : previous with
            {
                Health = health,
                ConsecutiveFailures = failures,
            };
    }

    private TimeSpan GetCadence()
    {
        AppSettings settings = _settings();
        return Volatile.Read(ref _reducedCadence) != 0
            ? settings.IdleInterval
            : settings.PollInterval;
    }

    private async Task WaitForRefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshRequests.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetRefreshing(bool refreshing)
    {
        Volatile.Write(ref _refreshing, refreshing ? 1 : 0);
        if (RefreshStateChanged is null)
        {
            return;
        }

        TaskCompletionSource? pump = EnqueueNotification(() =>
            InvokeHandlers(RefreshStateChanged, refreshing));
        pump?.TrySetResult();
    }

    private TaskCompletionSource? EnqueueReportUpdated(StatusReport report) =>
        EnqueueNotification(() => InvokeHandlers(ReportUpdated, report));

    private TaskCompletionSource? EnqueueNotification(Action notification)
    {
        lock (_notificationGate)
        {
            _notificationQueue.Enqueue(notification);
            if (_notificationPumpRunning)
            {
                return null;
            }

            _notificationPumpRunning = true;
            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _notificationPump = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                await DrainNotificationsAsync().ConfigureAwait(false);
            });
            return start;
        }
    }

    private static void StartNotificationPump(TaskCompletionSource start)
    {
        start.TrySetResult();
    }

    private async Task DrainNotificationsAsync()
    {
        try
        {
            while (TryDequeueNotification(out Action notification))
            {
                if (_synchronizationContext is null)
                {
                    notification();
                }
                else
                {
                    await InvokeNotificationOnContextAsync(notification).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            LogHandlerFailure(exception);
        }
    }

    private bool TryDequeueNotification(out Action notification)
    {
        lock (_notificationGate)
        {
            if (_notificationQueue.Count > 0)
            {
                notification = _notificationQueue.Dequeue();
                return true;
            }

            _notificationPumpRunning = false;
            notification = null!;
            return false;
        }
    }

    private Task InvokeNotificationOnContextAsync(Action notification)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _synchronizationContext!.Post(
                static state =>
                {
                    var dispatch = ((StatusPoller Poller, Action Notification, TaskCompletionSource Completion))state!;
                    try
                    {
                        dispatch.Notification();
                    }
                    catch (Exception exception)
                    {
                        dispatch.Poller.LogHandlerFailure(exception);
                    }
                    finally
                    {
                        dispatch.Completion.TrySetResult();
                    }
                },
                (this, notification, completion));
        }
        catch (Exception exception)
        {
            LogHandlerFailure(exception);
            completion.TrySetResult();
        }

        return completion.Task;
    }

    private void InvokeHandlers<T>(EventHandler<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<T> handler in handlers.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                handler(this, value);
            }
            catch (Exception exception)
            {
                LogHandlerFailure(exception);
            }
        }
    }

    private void LogHandlerFailure(Exception exception)
    {
        try
        {
            _log.Write(LogArea.Ui, LogOutcome.Failed, exception: exception);
        }
        catch (Exception)
        {
        }
    }

    private Task GetNotificationPump()
    {
        lock (_notificationGate)
        {
            return _notificationPump;
        }
    }

    private enum ProviderAttemptKind
    {
        Completed,
        CoolingDown,
        Unavailable,
        AvailabilityTimedOut,
        AvailabilityFailed,
        TimedOut,
        Failed,
    }

    private sealed record AvailabilityProbe(
        Task<bool> Task,
        CancellationTokenSource Cancellation);

    private sealed record ProviderAttempt(
        IStatusProvider Provider,
        ProviderSnapshot? Previous,
        ProviderAttemptKind Kind,
        ProviderFetchResult? Result = null,
        Exception? Exception = null,
        bool PreserveCooldown = false);
}
