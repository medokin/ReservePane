namespace ReservePane.Core;

internal interface IUiDispatcher
{
    void Post(Action action);
    Task InvokeAsync(Action action);
}

internal interface IHotkeyRegistration : IDisposable
{
    event EventHandler? Pressed;
    bool IsRegistered { get; }
}

internal readonly record struct ActivityCadenceSnapshot(long Version, bool IsReducedCadence);

internal readonly record struct PollLoopRun(Task Completion, Task Ready);

internal interface IActivityCadenceSource
{
    ActivityCadenceSnapshot Current { get; }
    ActivityCadenceSnapshot Subscribe(EventHandler handler);
    void Unsubscribe(EventHandler handler);
}

internal interface IActivityCadencePoller
{
    void SetReducedCadence(bool reduced);
    PollLoopRun Start(CancellationToken cancellationToken);
    void RequestRefresh();
}

internal sealed class ApplicationActivityCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly IActivityCadenceSource _activity;
    private readonly IActivityCadencePoller _poller;
    private readonly RollingFileLog _log;
    private long _appliedVersion = long.MinValue;
    private bool _started;
    private bool _subscribed;
    private bool _disposed;

    public ApplicationActivityCoordinator(
        IActivityCadenceSource activity,
        IActivityCadencePoller poller,
        RollingFileLog log)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public PollLoopRun Start(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The activity coordinator is already started.");
            }

            _started = true;
        }

        try
        {
            ActivityCadenceSnapshot initial = _activity.Subscribe(OnActivityChanged);
            lock (_gate)
            {
                _subscribed = true;
            }

            ApplySnapshot(initial);
            PollLoopRun pollLoop = _poller.Start(cancellationToken);
            _poller.RequestRefresh();
            return pollLoop;
        }
        catch
        {
            Unsubscribe();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Unsubscribe();
    }

    private void OnActivityChanged(object? sender, EventArgs args)
    {
        try
        {
            ApplySnapshot(_activity.Current);
        }
        catch (Exception exception)
        {
            TryLogFailure(exception);
        }
    }

    private void ApplySnapshot(ActivityCadenceSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed || snapshot.Version <= _appliedVersion)
            {
                return;
            }

            _poller.SetReducedCadence(snapshot.IsReducedCadence);
            _appliedVersion = snapshot.Version;
        }
    }

    private void Unsubscribe()
    {
        bool unsubscribe;
        lock (_gate)
        {
            unsubscribe = _subscribed;
            _subscribed = false;
        }

        if (unsubscribe)
        {
            _activity.Unsubscribe(OnActivityChanged);
        }
    }

    private void TryLogFailure(Exception exception)
    {
        try
        {
            _log.Write(LogArea.Platform, LogOutcome.Failed, exception: exception);
        }
        catch (Exception)
        {
        }
    }
}

internal sealed class AppSettingsState
{
    private AppSettings _current;

    public AppSettingsState(AppSettings initial)
    {
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    public AppSettings Current => Volatile.Read(ref _current);

    public void Update(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _current, settings);
    }
}

internal sealed class ApplicationSettingsCoordinator : IDisposable
{
    private readonly AppSettingsState _state;
    private readonly ThresholdWatcher _watcher;
    private readonly Func<string, IHotkeyRegistration> _createHotkey;
    private readonly Action<AppSettings> _applyOverlay;
    private readonly Action _requestRefresh;
    private readonly Func<Task> _toggleOverlay;
    private readonly RollingFileLog _log;
    private IHotkeyRegistration _hotkey;
    private bool _disposed;

    public ApplicationSettingsCoordinator(
        AppSettingsState state,
        ThresholdWatcher watcher,
        IHotkeyRegistration hotkey,
        Func<string, IHotkeyRegistration> createHotkey,
        Action<AppSettings> applyOverlay,
        Action requestRefresh,
        Func<Task> toggleOverlay,
        RollingFileLog log)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _createHotkey = createHotkey ?? throw new ArgumentNullException(nameof(createHotkey));
        _applyOverlay = applyOverlay ?? throw new ArgumentNullException(nameof(applyOverlay));
        _requestRefresh = requestRefresh ?? throw new ArgumentNullException(nameof(requestRefresh));
        _toggleOverlay = toggleOverlay ?? throw new ArgumentNullException(nameof(toggleOverlay));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _hotkey.Pressed += OnHotkeyPressed;
    }

    public void Apply(AppSettings next)
    {
        ArgumentNullException.ThrowIfNull(next);
        ObjectDisposedException.ThrowIf(_disposed, this);
        AppSettings previous = _state.Current;

        bool thresholdsChanged = previous.WarningPercent != next.WarningPercent ||
            previous.CriticalPercent != next.CriticalPercent;
        if (thresholdsChanged)
        {
            _watcher.UpdateThresholds(next.WarningPercent, next.CriticalPercent);
        }

        if (!string.Equals(previous.Hotkey, next.Hotkey, StringComparison.OrdinalIgnoreCase))
        {
            ReplaceHotkey(next.Hotkey);
        }

        _state.Update(next);

        if (OverlayChanged(previous, next))
        {
            try
            {
                _applyOverlay(next);
            }
            catch (Exception exception)
            {
                TryLogFailure(exception);
            }
        }

        if (thresholdsChanged || CadenceChanged(previous, next))
        {
            _requestRefresh();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hotkey.Pressed -= OnHotkeyPressed;
        _hotkey.Dispose();
    }

    private void ReplaceHotkey(string chord)
    {
        IHotkeyRegistration? replacement = null;
        try
        {
            replacement = _createHotkey(chord);
            if (!replacement.IsRegistered)
            {
                replacement.Dispose();
                TryLogFailure();
                return;
            }

            replacement.Pressed += OnHotkeyPressed;
            IHotkeyRegistration previous = _hotkey;
            _hotkey = replacement;
            replacement = null;
            previous.Pressed -= OnHotkeyPressed;
            previous.Dispose();
        }
        catch (Exception exception)
        {
            replacement?.Dispose();
            TryLogFailure(exception);
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs args) =>
        _ = ToggleOverlaySafelyAsync();

    private async Task ToggleOverlaySafelyAsync()
    {
        try
        {
            await _toggleOverlay();
        }
        catch (Exception exception)
        {
            TryLogFailure(exception);
        }
    }

    private void TryLogFailure(Exception? exception = null)
    {
        try
        {
            _log.Write(LogArea.Platform, LogOutcome.Failed, exception: exception);
        }
        catch (Exception)
        {
        }
    }

    private static bool OverlayChanged(AppSettings previous, AppSettings next) =>
        previous.OverlayVisible != next.OverlayVisible ||
        previous.OverlayCorner != next.OverlayCorner ||
        !string.Equals(previous.OverlayMonitorId, next.OverlayMonitorId, StringComparison.Ordinal) ||
        previous.OverlayPosition != next.OverlayPosition;

    private static bool CadenceChanged(AppSettings previous, AppSettings next) =>
        previous.PollInterval != next.PollInterval ||
        previous.IdleInterval != next.IdleInterval;
}

internal sealed class ApplicationShutdownCoordinator
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _applicationCancellation;
    private readonly Func<Task?> _getPollLoop;
    private readonly Func<Task> _flushPositionPersistence;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action _disposeOwnedResources;
    private readonly Action _shutdownApplication;
    private readonly RollingFileLog _log;
    private Task? _shutdownTask;
    private Task? _positionFlushTask;
    private int _ownedResourcesDisposed;

    public ApplicationShutdownCoordinator(
        CancellationTokenSource applicationCancellation,
        Func<Task?> getPollLoop,
        Func<Task> flushPositionPersistence,
        IUiDispatcher dispatcher,
        Action disposeOwnedResources,
        Action shutdownApplication,
        RollingFileLog log)
    {
        _applicationCancellation = applicationCancellation ?? throw new ArgumentNullException(nameof(applicationCancellation));
        _getPollLoop = getPollLoop ?? throw new ArgumentNullException(nameof(getPollLoop));
        _flushPositionPersistence = flushPositionPersistence ?? throw new ArgumentNullException(nameof(flushPositionPersistence));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _disposeOwnedResources = disposeOwnedResources ?? throw new ArgumentNullException(nameof(disposeOwnedResources));
        _shutdownApplication = shutdownApplication ?? throw new ArgumentNullException(nameof(shutdownApplication));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task ShutdownAsync()
    {
        lock (_gate)
        {
            return _shutdownTask ??= RunShutdownAsync();
        }
    }

    public void ShutdownFallback()
    {
        CancelApplication();
        ObservePositionFlushBestEffort(BeginPositionFlushOnce());
        DisposeOwnedResourcesOnce();
    }

    private async Task RunShutdownAsync()
    {
        CancelApplication();
        Task? pollLoop = _getPollLoop();
        if (pollLoop is not null)
        {
            try
            {
                await pollLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_applicationCancellation.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                // PollLoopFaultObserver owns fault logging. This coordinator only waits for termination.
            }
        }

        await FlushPositionPersistenceSafelyAsync().ConfigureAwait(false);

        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                DisposeOwnedResourcesOnce();
                _shutdownApplication();
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TryLogFailure(exception);
        }
    }

    private async Task FlushPositionPersistenceSafelyAsync()
    {
        try
        {
            await BeginPositionFlushOnce().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TryLogFailure(exception);
        }
    }

    private Task BeginPositionFlushOnce()
    {
        lock (_gate)
        {
            if (_positionFlushTask is not null)
            {
                return _positionFlushTask;
            }

            try
            {
                _positionFlushTask = _flushPositionPersistence()
                    ?? Task.FromException(new InvalidOperationException("Position persistence returned no task."));
            }
            catch (Exception exception)
            {
                _positionFlushTask = Task.FromException(exception);
            }

            return _positionFlushTask;
        }
    }

    private void ObserveCompletedPositionFlush(Task flush)
    {
        if (!flush.IsCompleted)
        {
            return;
        }

        try
        {
            flush.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            TryLogFailure(exception);
        }
    }

    private void ObservePositionFlushBestEffort(Task flush)
    {
        if (flush.IsCompleted)
        {
            ObserveCompletedPositionFlush(flush);
            return;
        }

        _ = flush.ContinueWith(
            static (completed, state) =>
                ((ApplicationShutdownCoordinator)state!).ObserveCompletedPositionFlush(completed),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CancelApplication()
    {
        try
        {
            _applicationCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void DisposeOwnedResourcesOnce()
    {
        if (Interlocked.Exchange(ref _ownedResourcesDisposed, 1) == 0)
        {
            _disposeOwnedResources();
        }
    }

    private void TryLogFailure(Exception exception)
    {
        try
        {
            _log.Write(LogArea.Application, LogOutcome.Failed, exception: exception);
        }
        catch (Exception)
        {
        }
    }
}

internal sealed class PollLoopFaultObserver
{
    private readonly Func<Task> _requestShutdown;
    private readonly RollingFileLog _log;

    public PollLoopFaultObserver(Func<Task> requestShutdown, RollingFileLog log)
    {
        _requestShutdown = requestShutdown ?? throw new ArgumentNullException(nameof(requestShutdown));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task ObserveAsync(Task pollLoop, CancellationToken applicationCancellation)
    {
        ArgumentNullException.ThrowIfNull(pollLoop);
        return ObserveCoreAsync(pollLoop, applicationCancellation);
    }

    private async Task ObserveCoreAsync(Task pollLoop, CancellationToken applicationCancellation)
    {
        try
        {
            await pollLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (applicationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            TryLogFailure(exception);
            try
            {
                await _requestShutdown().ConfigureAwait(false);
            }
            catch (Exception shutdownException)
            {
                TryLogFailure(shutdownException);
            }
        }
    }

    private void TryLogFailure(Exception exception)
    {
        try
        {
            _log.Write(LogArea.Application, LogOutcome.Failed, exception: exception);
        }
        catch (Exception)
        {
        }
    }
}
