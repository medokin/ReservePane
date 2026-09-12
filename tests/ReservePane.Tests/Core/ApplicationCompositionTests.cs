using ReservePane.Core;
using ReservePane.Model;
using ReservePane.Tests.Support;
using ReservePane.Ui;
using static ReservePane.Tests.Support.SnapshotFactory;

namespace ReservePane.Tests.Core;

public sealed class ApplicationCompositionTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Fact]
    public void SettingsReload_UpdatesStatefulServicesAndReregistersHotkeyTransactionally()
    {
        AppSettings initial = AppSettings.Default;
        var state = new AppSettingsState(initial);
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset cycle = DateTimeOffset.Parse("2026-08-29T01:59:59Z");
        Assert.Single(watcher.Evaluate(Report(79, cycle), Report(80, cycle)));
        var originalHotkey = new FakeHotkeyRegistration(isRegistered: true);
        var rejectedHotkey = new FakeHotkeyRegistration(isRegistered: false);
        var acceptedHotkey = new FakeHotkeyRegistration(isRegistered: true);
        var created = new Queue<FakeHotkeyRegistration>([rejectedHotkey, acceptedHotkey]);
        var overlaySettings = new List<AppSettings>();
        int refreshes = 0;
        int toggles = 0;
        using var coordinator = new ApplicationSettingsCoordinator(
            state,
            watcher,
            originalHotkey,
            _ => created.Dequeue(),
            overlaySettings.Add,
            () => refreshes++,
            () =>
            {
                toggles++;
                return Task.CompletedTask;
            },
            CreateLog());

        AppSettings rejected = initial with { Hotkey = "Ctrl+Shift+Q" };
        coordinator.Apply(rejected);

        Assert.True(rejectedHotkey.Disposed);
        Assert.False(originalHotkey.Disposed);
        originalHotkey.RaisePressed();
        Assert.Equal(1, toggles);

        AppSettings accepted = rejected with
        {
            Hotkey = "Win+Shift+9",
            WarningPercent = 70,
            CriticalPercent = 90,
            PollInterval = TimeSpan.FromSeconds(45),
            OverlayVisible = true,
            OverlayCorner = OverlayCorner.TopLeft,
        };
        coordinator.Apply(accepted);

        Assert.True(originalHotkey.Disposed);
        Assert.False(acceptedHotkey.Disposed);
        acceptedHotkey.RaisePressed();
        Assert.Equal(2, toggles);
        Assert.Equal(accepted, state.Current);
        Assert.Equal(1, refreshes);
        Assert.Equal(accepted, Assert.Single(overlaySettings));
        Assert.Equal(
            AlertKind.Critical,
            Assert.Single(watcher.Evaluate(Report(80, cycle), Report(91, cycle))).Kind);
    }

    [Fact]
    public void SettingsReload_EquivalentAppOwnedSaveDoesNotCreateFeedbackWork()
    {
        AppSettings initial = AppSettings.Default;
        var state = new AppSettingsState(initial);
        var hotkey = new FakeHotkeyRegistration(isRegistered: true);
        int factories = 0;
        int overlays = 0;
        int refreshes = 0;
        using var coordinator = new ApplicationSettingsCoordinator(
            state,
            new ThresholdWatcher(80, 95),
            hotkey,
            _ =>
            {
                factories++;
                return new FakeHotkeyRegistration(isRegistered: true);
            },
            _ => overlays++,
            () => refreshes++,
            () => Task.CompletedTask,
            CreateLog());
        AppSettings equivalentReload = initial with { };

        coordinator.Apply(equivalentReload);

        Assert.Equal(0, factories);
        Assert.Equal(0, overlays);
        Assert.Equal(0, refreshes);
    }

    [Fact]
    public void SettingsReload_ThresholdOnlyChangeRequestsPromptRefresh()
    {
        AppSettings initial = AppSettings.Default;
        int refreshes = 0;
        using var coordinator = new ApplicationSettingsCoordinator(
            new AppSettingsState(initial),
            new ThresholdWatcher(80, 95),
            new FakeHotkeyRegistration(isRegistered: true),
            _ => new FakeHotkeyRegistration(isRegistered: true),
            _ => { },
            () => refreshes++,
            () => Task.CompletedTask,
            CreateLog());

        coordinator.Apply(initial with { WarningPercent = 75 });

        Assert.Equal(1, refreshes);
    }

    [Fact]
    public void SettingsReload_OverlayFailureDoesNotBlockCadenceRefresh()
    {
        AppSettings initial = AppSettings.Default;
        int refreshes = 0;
        using var coordinator = new ApplicationSettingsCoordinator(
            new AppSettingsState(initial),
            new ThresholdWatcher(80, 95),
            new FakeHotkeyRegistration(isRegistered: true),
            _ => new FakeHotkeyRegistration(isRegistered: true),
            _ => throw new InvalidOperationException("Synthetic placement failure."),
            () => refreshes++,
            () => Task.CompletedTask,
            CreateLog());

        coordinator.Apply(initial with
        {
            OverlayVisible = true,
            PollInterval = TimeSpan.FromSeconds(30),
        });

        Assert.Equal(1, refreshes);
    }

    [Theory]
    [InlineData(ActivityTransitionBoundary.BeforeSubscription, 1)]
    [InlineData(ActivityTransitionBoundary.BetweenSnapshotAndInitialApplication, 1)]
    [InlineData(ActivityTransitionBoundary.AfterSubscriptionBeforeRun, 1)]
    [InlineData(ActivityTransitionBoundary.JustAfterRun, 2)]
    public void ActivityStartup_TransitionsAreAppliedWithoutStaleOverwrite(
        ActivityTransitionBoundary boundary,
        int expectedRefreshes)
    {
        // Break caught: an activity transition is missed or overwritten during startup wiring.
        var activity = new FakeActivityCadenceSource();
        var poller = new FakeActivityCadencePoller();

        if (boundary == ActivityTransitionBoundary.BeforeSubscription)
        {
            activity.TransitionTo(reduced: true);
        }
        else if (boundary == ActivityTransitionBoundary.BetweenSnapshotAndInitialApplication)
        {
            activity.AfterSubscriptionSnapshot = () => activity.TransitionTo(reduced: true);
        }
        else if (boundary == ActivityTransitionBoundary.AfterSubscriptionBeforeRun)
        {
            poller.AfterInitialCadenceApplication = () => activity.TransitionTo(reduced: true);
        }
        else
        {
            poller.AfterRunStarted = () => activity.TransitionTo(reduced: true);
        }

        using var coordinator = new ApplicationActivityCoordinator(activity, poller, CreateLog());
        PollLoopRun pollLoop = coordinator.Start(CancellationToken.None);

        Assert.Same(poller.RawPollLoop, pollLoop.Completion);
        Assert.Same(poller.Ready, pollLoop.Ready);
        Assert.True(poller.IsReducedCadence);
        Assert.Equal(expectedRefreshes, poller.RefreshCount);
    }

    [Fact]
    public void ActivityStartup_DisposeRemovesItsSubscription()
    {
        // Break caught: activity notifications continue changing the poller after application disposal.
        var activity = new FakeActivityCadenceSource();
        var poller = new FakeActivityCadencePoller();
        var coordinator = new ApplicationActivityCoordinator(activity, poller, CreateLog());
        coordinator.Start(CancellationToken.None);

        coordinator.Dispose();
        activity.TransitionTo(reduced: true);

        Assert.False(poller.IsReducedCadence);
        Assert.Equal(1, poller.RefreshCount);
        Assert.Equal(0, activity.SubscriberCount);
    }

    [Fact]
    public async Task Shutdown_CancelsAndAwaitsPollLoopBeforeUiDisposalAndApplicationShutdown()
    {
        using var cancellation = new CancellationTokenSource();
        var pollReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flushReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new QueuedDispatcher();
        var events = new List<string>();
        Task pollLoop = WaitForCancellationAndReleaseAsync();
        var coordinator = new ApplicationShutdownCoordinator(
            cancellation,
            () => pollLoop,
            FlushPositionAsync,
            dispatcher,
            () => events.Add("dispose"),
            () => events.Add("shutdown"),
            CreateLog());

        Task first = coordinator.ShutdownAsync();
        Task second = coordinator.ShutdownAsync();
        await Task.Delay(20);

        Assert.Same(first, second);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Empty(events);
        Assert.Equal(0, dispatcher.PendingCount);

        pollReleased.TrySetResult();
        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["poll"], events);
        Assert.Equal(0, dispatcher.PendingCount);

        flushReleased.TrySetResult();
        await dispatcher.WaitForPendingAsync();
        Assert.False(first.IsCompleted);
        dispatcher.RunNext();
        await first;

        Assert.Equal(["poll", "flush", "dispose", "shutdown"], events);

        async Task WaitForCancellationAndReleaseAsync()
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token)
                .ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await pollReleased.Task;
            events.Add("poll");
        }

        async Task FlushPositionAsync()
        {
            flushStarted.TrySetResult();
            await flushReleased.Task;
            events.Add("flush");
        }
    }

    [Fact]
    public async Task Shutdown_PositionFlushFailureIsLoggedAndDoesNotBlockDisposal()
    {
        // Break caught: a final persistence failure prevents SettingsStore disposal or WPF shutdown.
        using var cancellation = new CancellationTokenSource();
        var dispatcher = new QueuedDispatcher();
        var events = new List<string>();
        string logPath = Path.Combine(_directory.Path, "flush-failure.log");
        var coordinator = new ApplicationShutdownCoordinator(
            cancellation,
            () => Task.CompletedTask,
            () => Task.FromException(new IOException("path=user@example.test token=secret")),
            dispatcher,
            () => events.Add("dispose"),
            () => events.Add("shutdown"),
            new RollingFileLog(logPath));

        Task shutdown = coordinator.ShutdownAsync();
        await dispatcher.WaitForPendingAsync();
        dispatcher.RunNext();
        await shutdown;

        Assert.Equal(["dispose", "shutdown"], events);
        string log = File.ReadAllText(logPath);
        Assert.Contains(" application failed exception=IOException", log, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PollLoopFault_IsObservedAndTriggersExactlyOneOrderlyShutdown()
    {
        // Break caught: a later poll-loop fault remains unobserved while the tray silently goes stale.
        using var cancellation = new CancellationTokenSource();
        var pollCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new QueuedDispatcher();
        int disposals = 0;
        int shutdowns = 0;
        string logPath = Path.Combine(_directory.Path, $"poll-fault-{Guid.NewGuid():N}.log");
        var log = new RollingFileLog(logPath);
        var coordinator = new ApplicationShutdownCoordinator(
            cancellation,
            () => pollCompletion.Task,
            () => Task.CompletedTask,
            dispatcher,
            () => disposals++,
            () => shutdowns++,
            log);
        var observer = new PollLoopFaultObserver(coordinator.ShutdownAsync, log);

        Task observation = observer.ObserveAsync(pollCompletion.Task, cancellation.Token);
        pollCompletion.SetException(new InvalidOperationException("token=secret account=user@example.test"));

        await dispatcher.WaitForPendingAsync();
        dispatcher.RunNext();
        await observation;

        Assert.True(pollCompletion.Task.IsFaulted);
        Assert.True(observation.IsCompletedSuccessfully);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, disposals);
        Assert.Equal(1, shutdowns);
        string[] failureLines = File.ReadAllLines(logPath)
            .Where(line => line.Contains("application failed", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(failureLines);
        Assert.Contains("exception=InvalidOperationException", failureLines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failureLines[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test", failureLines[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PollLoopCancellation_DoesNotLogOrRequestRecursiveShutdown()
    {
        // Break caught: normal application cancellation is treated as a poll failure and starts shutdown twice.
        using var cancellation = new CancellationTokenSource();
        var pollCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int shutdowns = 0;
        string logPath = Path.Combine(_directory.Path, $"poll-cancel-{Guid.NewGuid():N}.log");
        var observer = new PollLoopFaultObserver(
            () =>
            {
                shutdowns++;
                return Task.CompletedTask;
            },
            new RollingFileLog(logPath));

        Task observation = observer.ObserveAsync(pollCompletion.Task, cancellation.Token);
        cancellation.Cancel();
        pollCompletion.SetCanceled(cancellation.Token);
        await observation;

        Assert.True(observation.IsCompletedSuccessfully);
        Assert.Equal(0, shutdowns);
        Assert.False(File.Exists(logPath));
    }

    [Fact]
    public void ShutdownFallback_IsSynchronousIdempotentAndDoesNotRequestShutdownAgain()
    {
        using var cancellation = new CancellationTokenSource();
        int disposals = 0;
        int shutdowns = 0;
        int flushes = 0;
        var coordinator = new ApplicationShutdownCoordinator(
            cancellation,
            () => Task.CompletedTask,
            () =>
            {
                flushes++;
                return Task.CompletedTask;
            },
            new QueuedDispatcher(),
            () => disposals++,
            () => shutdowns++,
            CreateLog());

        coordinator.ShutdownFallback();
        coordinator.ShutdownFallback();

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, flushes);
        Assert.Equal(1, disposals);
        Assert.Equal(0, shutdowns);
    }

    [Fact]
    public async Task ShutdownFallback_DoesNotBlockAndObservesLaterFlushFailure()
    {
        // Break caught: forced exit either blocks the UI on disk I/O or abandons a later flush exception unobserved.
        using var cancellation = new CancellationTokenSource();
        var flush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string logPath = Path.Combine(_directory.Path, "fallback-flush.log");
        int disposals = 0;
        var coordinator = new ApplicationShutdownCoordinator(
            cancellation,
            () => Task.CompletedTask,
            () => flush.Task,
            new QueuedDispatcher(),
            () => disposals++,
            () => throw new Xunit.Sdk.XunitException("Fallback must not request WPF shutdown."),
            new RollingFileLog(logPath));

        coordinator.ShutdownFallback();

        Assert.Equal(1, disposals);
        Assert.False(flush.Task.IsCompleted);
        flush.TrySetException(new IOException("path=user@example.test token=secret"));
        string log = await ReadLogEventuallyAsync(logPath, "application failed exception=IOException");

        Assert.Contains(" application failed exception=IOException", log, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test", log, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _directory.Dispose();

    private RollingFileLog CreateLog() =>
        new(Path.Combine(_directory.Path, $"composition-{Guid.NewGuid():N}.log"));

    private static async Task<string> ReadLogEventuallyAsync(string path, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            try
            {
                if (File.Exists(path))
                {
                    string text = File.ReadAllText(path);
                    if (text.Contains(expected, StringComparison.Ordinal))
                    {
                        await Task.Delay(10, timeout.Token);
                        return text;
                    }
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeHotkeyRegistration(bool isRegistered) : IHotkeyRegistration
    {
        public event EventHandler? Pressed;
        public bool IsRegistered { get; } = isRegistered;
        public bool Disposed { get; private set; }
        public void RaisePressed() => Pressed?.Invoke(this, EventArgs.Empty);
        public void Dispose() => Disposed = true;
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<(Action Action, TaskCompletionSource Completion)> _pending = [];
        private readonly SemaphoreSlim _available = new(0);
        public int PendingCount => _pending.Count;
        public void Post(Action action)
        {
            _pending.Enqueue((action, new TaskCompletionSource()));
            _available.Release();
        }

        public Task InvokeAsync(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue((action, completion));
            _available.Release();
            return completion.Task;
        }

        public async Task WaitForPendingAsync() =>
            Assert.True(
                await _available.WaitAsync(TimeSpan.FromSeconds(10)),
                "The dispatcher action was not queued before the test timeout.");

        public void RunNext()
        {
            (Action action, TaskCompletionSource completion) = _pending.Dequeue();
            action();
            completion.TrySetResult();
        }
    }

    public enum ActivityTransitionBoundary
    {
        BeforeSubscription,
        BetweenSnapshotAndInitialApplication,
        AfterSubscriptionBeforeRun,
        JustAfterRun,
    }

    private sealed class FakeActivityCadenceSource : IActivityCadenceSource
    {
        private EventHandler? _changed;
        private long _version;
        private bool _reduced;

        public Action? AfterSubscriptionSnapshot { get; set; }
        public int SubscriberCount => _changed?.GetInvocationList().Length ?? 0;

        public ActivityCadenceSnapshot Current => new(_version, _reduced);

        public ActivityCadenceSnapshot Subscribe(EventHandler handler)
        {
            _changed += handler;
            ActivityCadenceSnapshot snapshot = Current;
            AfterSubscriptionSnapshot?.Invoke();
            return snapshot;
        }

        public void Unsubscribe(EventHandler handler) => _changed -= handler;

        public void TransitionTo(bool reduced)
        {
            _reduced = reduced;
            _version++;
            _changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeActivityCadencePoller : IActivityCadencePoller
    {
        private bool _running;
        private int _cadenceApplications;

        public Action? AfterInitialCadenceApplication { get; set; }
        public Action? AfterRunStarted { get; set; }
        public bool IsReducedCadence { get; private set; }
        public int RefreshCount { get; private set; }
        public Task RawPollLoop { get; } = new TaskCompletionSource().Task;
        public Task Ready { get; } = Task.CompletedTask;

        public void SetReducedCadence(bool reduced)
        {
            bool changed = IsReducedCadence != reduced;
            IsReducedCadence = reduced;
            if (_running && changed)
            {
                RefreshCount++;
            }

            if (Interlocked.Increment(ref _cadenceApplications) == 1)
            {
                AfterInitialCadenceApplication?.Invoke();
            }
        }

        public PollLoopRun Start(CancellationToken cancellationToken)
        {
            _running = true;
            AfterRunStarted?.Invoke();
            return new PollLoopRun(RawPollLoop, Ready);
        }

        public void RequestRefresh() => RefreshCount++;
    }
}
