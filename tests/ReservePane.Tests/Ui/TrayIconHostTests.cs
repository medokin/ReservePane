using System.Collections.Immutable;
using System.Diagnostics;
using ReservePane.Core;
using ReservePane.Model;
using ReservePane.Platform;
using ReservePane.Tests.Support;
using ReservePane.Ui;

namespace ReservePane.Tests.Ui;

public sealed class TrayIconHostTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Fact]
    public void WinFormsTrayIconView_InitialTooltipUsesReservePaneIdentity()
    {
        using var view = new WinFormsTrayIconView();

        Assert.Equal("ReservePane", view.Tooltip);
    }

    [Fact]
    public void MenuDefinition_UsesExactItemsOrderAndCheckability()
    {
        Assert.Collection(
            TrayMenuDefinition.Items,
            item => Assert.Equal((TrayCommand.ToggleOverlay, "Show corner overlay", true), (item.Command, item.Text, item.Checkable)),
            item => Assert.Equal((TrayCommand.ToggleAutostart, "Start with Windows", true), (item.Command, item.Text, item.Checkable)),
            item => Assert.Equal((TrayCommand.Refresh, "Refresh now", false), (item.Command, item.Text, item.Checkable)),
            item => Assert.Equal((TrayCommand.OpenSettings, "Settings file", false), (item.Command, item.Text, item.Checkable)),
            item => Assert.Equal((TrayCommand.Exit, "Exit", false), (item.Command, item.Text, item.Checkable)));
    }

    [Fact]
    public void LeftClick_TogglesPopupAndMenuReadsLiveState()
    {
        using var harness = new HostHarness(_directory.Path);
        harness.AutostartEnabled = false;

        harness.View.RaiseLeftClick();
        harness.View.RaiseMenuOpening();

        Assert.True(harness.Popup.IsVisible);
        Assert.False(harness.View.OverlayChecked);
        Assert.False(harness.View.AutostartChecked);

        harness.AutostartEnabled = true;
        harness.Overlay.Show();
        harness.View.RaiseMenuOpening();
        harness.View.RaiseLeftClick();

        Assert.True(harness.View.OverlayChecked);
        Assert.True(harness.View.AutostartChecked);
        Assert.False(harness.Popup.IsVisible);
    }

    [Fact]
    public void WindowRefreshRequests_RefreshWithoutClosingEitherWindow()
    {
        using var harness = new HostHarness(_directory.Path);
        harness.Popup.Show();
        harness.Overlay.Show();

        harness.Popup.RaiseRefreshRequest();
        harness.Overlay.RaiseRefreshRequest();

        Assert.Equal(2, harness.RefreshRequests);
        Assert.True(harness.Popup.IsVisible);
        Assert.True(harness.Overlay.IsVisible);
        Assert.Equal(0, harness.ShutdownRequests);
    }

    [Fact]
    public async Task WindowCloseRequests_HideToTrayAndPersistOverlayVisibilityOnly()
    {
        using var harness = new HostHarness(_directory.Path);
        await harness.Store.SaveAsync(AppSettings.Default with
        {
            OverlayVisible = true,
            Hotkey = "Ctrl+Shift+Q",
        }, CancellationToken.None);
        harness.Popup.Show();
        harness.Overlay.Show();

        harness.Popup.RaiseCloseRequest();
        Assert.False(harness.Popup.IsVisible);
        Assert.True(harness.Overlay.IsVisible);

        harness.Overlay.RaiseCloseRequest();
        Assert.False(harness.Overlay.IsVisible);
        await harness.Overlay.SettingsApplied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AppSettings saved = await harness.Store.LoadAsync(CancellationToken.None);

        Assert.False(saved.OverlayVisible);
        Assert.Equal("Ctrl+Shift+Q", saved.Hotkey);
        Assert.False(harness.View.OverlayChecked);
        Assert.Equal(0, harness.ShutdownRequests);
        Assert.True(harness.View.Visible);
    }

    [Fact]
    public void RefreshStateDelivery_UpdatesBothWindowsAndStopsAfterDisposal()
    {
        using var harness = new HostHarness(_directory.Path);

        harness.Reports.RaiseRefreshState(true);
        Assert.Equal(1, harness.Dispatcher.PendingCount);
        harness.Dispatcher.RunNext();
        Assert.True(harness.Popup.IsRefreshing);
        Assert.True(harness.Overlay.IsRefreshing);

        harness.Reports.RaiseRefreshState(false);
        harness.Dispatcher.RunNext();
        Assert.False(harness.Popup.IsRefreshing);
        Assert.False(harness.Overlay.IsRefreshing);

        harness.Host.Dispose();
        harness.Popup.RaiseRefreshRequest();
        harness.Overlay.RaiseRefreshRequest();
        harness.Reports.RaiseRefreshState(true);
        Assert.Equal(0, harness.RefreshRequests);
        Assert.Equal(0, harness.Dispatcher.PendingCount);
    }

    [Fact]
    public async Task Commands_UseNarrowPersistenceAndRouteEveryAction()
    {
        using var harness = new HostHarness(_directory.Path);
        AppSettings external = AppSettings.Default with
        {
            Hotkey = "Ctrl+Shift+Q",
            WarningPercent = 72,
        };
        await harness.Store.SaveAsync(external, CancellationToken.None);

        await harness.Host.ExecuteCommandAsync(TrayCommand.ToggleOverlay);
        await harness.Host.ExecuteCommandAsync(TrayCommand.ToggleAutostart);
        await harness.Host.ExecuteCommandAsync(TrayCommand.Refresh);
        await harness.Host.ExecuteCommandAsync(TrayCommand.OpenSettings);
        await harness.Host.ExecuteCommandAsync(TrayCommand.Exit);

        AppSettings saved = await harness.Store.LoadAsync(CancellationToken.None);
        Assert.True(saved.OverlayVisible);
        Assert.True(saved.Autostart);
        Assert.Equal("Ctrl+Shift+Q", saved.Hotkey);
        Assert.Equal(72, saved.WarningPercent);
        Assert.True(harness.Overlay.IsVisible);
        Assert.True(harness.AutostartEnabled);
        Assert.Equal(1, harness.RefreshRequests);
        Assert.Equal(1, harness.OpenSettingsRequests);
        Assert.Equal(1, harness.ShutdownRequests);
    }

    [Fact]
    public void ReportDelivery_MarshalsAndUpdatesBothWindowsTrayAndAlerts()
    {
        using var harness = new HostHarness(_directory.Path);
        StatusReport report = CreateReport();

        harness.Reports.Raise(report);

        Assert.Empty(harness.Popup.Providers);
        Assert.Equal(1, harness.Dispatcher.PendingCount);

        harness.Dispatcher.RunNext();

        Assert.Equal(report.Providers, harness.Popup.Providers);
        Assert.Equal(report.Providers, harness.Overlay.Providers);
        Assert.Equal(TimeSpan.FromSeconds(60), harness.Popup.PollInterval);
        Assert.Equal(TrayState.Red, harness.View.State);
        Assert.Equal("Claude 95%\nCodex 2%\nOllama", harness.View.Tooltip);
        Assert.Equal(AlertKind.Critical, Assert.Single(harness.Alerts).Kind);

        harness.Reports.Raise(report);
        harness.Dispatcher.RunNext();
        Assert.Single(harness.Alerts);
    }

    [Fact]
    public void ReportDelivery_ReplacesBothWindowsWithFilteredProviderReport()
    {
        using var harness = new HostHarness(_directory.Path);
        StatusReport initialReport = CreateReport();
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        StatusReport filteredReport = new(
            now,
            [Snapshot("codex", "Codex", HealthState.Ok, 2, DateTimeOffset.Parse("2026-08-29T01:59:59Z"))]);

        harness.Reports.Raise(initialReport);
        harness.Dispatcher.RunNext();
        Assert.Equal(["claude", "codex", "ollama"], harness.Popup.Providers.Select(provider => provider.Id));
        Assert.Equal(["claude", "codex", "ollama"], harness.Overlay.Providers.Select(provider => provider.Id));

        harness.Reports.Raise(filteredReport);
        harness.Dispatcher.RunNext();

        Assert.Equal(["codex"], harness.Popup.Providers.Select(provider => provider.Id));
        Assert.Equal(["codex"], harness.Overlay.Providers.Select(provider => provider.Id));
    }

    [Fact]
    public void Dispose_UnsubscribesAndInvalidatesQueuedReportDelivery()
    {
        using var harness = new HostHarness(_directory.Path);
        harness.Reports.Raise(CreateReport());

        harness.Host.Dispose();
        harness.Dispatcher.RunNext();
        harness.Reports.Raise(CreateReport());

        Assert.Empty(harness.Popup.Providers);
        Assert.Equal(0, harness.Dispatcher.PendingCount);
        Assert.False(harness.View.Visible);
        Assert.Equal(1, harness.View.DisposeCalls);
    }

    [Fact]
    public async Task SettingsFileLauncher_CreatesDefaultsAndUsesShellExecution()
    {
        string path = Path.Combine(_directory.Path, "launcher", "settings.json");
        using var store = new SettingsStore(path);
        var process = new FakeProcessLauncher();
        var launcher = new SettingsFileLauncher(store, path, process);

        await launcher.OpenAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(path, process.StartInfo?.FileName);
        Assert.True(process.StartInfo?.UseShellExecute);
    }

    [Fact]
    public void Constructor_VisibleFailureRollsBackSubscriptionsAndOwnedView()
    {
        var view = new FakeTrayIconView { VisibleFailure = new InvalidOperationException("Synthetic tray failure.") };
        var reports = new FakeReportSource();
        using var store = new SettingsStore(Path.Combine(_directory.Path, "constructor-settings.json"));

        Assert.Throws<InvalidOperationException>(() => new TrayIconHost(
            view,
            new QueuedDispatcher(),
            new FakeStatusWindow(),
            new FakeOverlayWindow(),
            reports,
            new ThresholdWatcher(80, 95),
            () => AppSettings.Default,
            () => TimeSpan.FromSeconds(60),
            store.UpdateAsync,
            () => false,
            _ => { },
            () => { },
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            _ => { },
            new RollingFileLog(Path.Combine(_directory.Path, "constructor.log"))));

        Assert.Equal(0, reports.SubscriberCount);
        Assert.Equal(1, view.DisposeCalls);
    }

    public void Dispose() => _directory.Dispose();

    private static StatusReport CreateReport()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        DateTimeOffset reset = DateTimeOffset.Parse("2026-08-29T01:59:59Z");
        return new StatusReport(
            now,
            [
                Snapshot("claude", "Claude", HealthState.Ok, 95, reset),
                Snapshot("codex", "Codex", HealthState.Ok, 2, reset),
                Snapshot("ollama", "Ollama", HealthState.Ok, null, null),
            ]);
    }

    private static ProviderSnapshot Snapshot(
        string id,
        string label,
        HealthState health,
        double? percent,
        DateTimeOffset? reset) =>
        new(
            id,
            label,
            health,
            null,
            percent is null ? [] : [new UsageWindow("weekly", percent, reset, Severity.Normal)],
            [],
            null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
            0);

    private sealed class HostHarness : IDisposable
    {
        public HostHarness(string directory)
        {
            Store = new SettingsStore(Path.Combine(directory, $"settings-{Guid.NewGuid():N}.json"));
            Store.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
            Log = new RollingFileLog(Path.Combine(directory, $"log-{Guid.NewGuid():N}.txt"));
            Host = new TrayIconHost(
                View,
                Dispatcher,
                Popup,
                Overlay,
                Reports,
                new ThresholdWatcher(80, 95),
                () => AppSettings.Default,
                () => TimeSpan.FromSeconds(60),
                Store.UpdateAsync,
                () => AutostartEnabled,
                enabled => AutostartEnabled = enabled,
                () => RefreshRequests++,
                () =>
                {
                    OpenSettingsRequests++;
                    return Task.CompletedTask;
                },
                () =>
                {
                    ShutdownRequests++;
                    return Task.CompletedTask;
                },
                Alerts.Add,
                Log);
        }

        public FakeTrayIconView View { get; } = new();
        public QueuedDispatcher Dispatcher { get; } = new();
        public FakeStatusWindow Popup { get; } = new();
        public FakeOverlayWindow Overlay { get; } = new();
        public FakeReportSource Reports { get; } = new();
        public SettingsStore Store { get; }
        public RollingFileLog Log { get; }
        public TrayIconHost Host { get; }
        public List<StatusAlert> Alerts { get; } = [];
        public bool AutostartEnabled { get; set; }
        public int RefreshRequests { get; set; }
        public int OpenSettingsRequests { get; set; }
        public int ShutdownRequests { get; set; }

        public void Dispose()
        {
            Host.Dispose();
            Store.Dispose();
        }
    }

    private sealed class FakeTrayIconView : ITrayIconView
    {
        private bool _visible;
        public event Action? LeftClicked;
        public event Action? MenuOpening;
        public event Action<TrayCommand>? CommandInvoked;
        public Exception? VisibleFailure { get; init; }
        public bool Visible
        {
            get => _visible;
            set
            {
                if (VisibleFailure is not null)
                {
                    throw VisibleFailure;
                }

                _visible = value;
            }
        }
        public bool OverlayChecked { get; set; }
        public bool AutostartChecked { get; set; }
        public TrayState State { get; set; }
        public string Tooltip { get; set; } = string.Empty;
        public int DisposeCalls { get; private set; }

        public void RaiseLeftClick() => LeftClicked?.Invoke();
        public void RaiseMenuOpening() => MenuOpening?.Invoke();
        public void RaiseCommand(TrayCommand command) => CommandInvoked?.Invoke(command);
        public void Dispose() => DisposeCalls++;
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<(Action Action, TaskCompletionSource? Completion)> _pending = [];
        public int PendingCount => _pending.Count;
        public void Post(Action action) => _pending.Enqueue((action, null));

        public Task InvokeAsync(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue((action, completion));
            return completion.Task;
        }

        public void RunNext()
        {
            (Action action, TaskCompletionSource? completion) = _pending.Dequeue();
            action();
            completion?.TrySetResult();
        }
    }

    private class FakeStatusWindow : IStatusWindow
    {
        public event EventHandler? RefreshRequested;
        public event EventHandler? CloseRequested;
        public bool IsVisible { get; private set; }
        public bool IsRefreshing { get; private set; }
        public ImmutableArray<ProviderSnapshot> Providers { get; private set; } = [];
        public TimeSpan PollInterval { get; private set; }
        public void Show() => IsVisible = true;
        public void Hide() => IsVisible = false;
        public void SetRefreshing(bool refreshing) => IsRefreshing = refreshing;
        public void RaiseRefreshRequest() => RefreshRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseCloseRequest() => CloseRequested?.Invoke(this, EventArgs.Empty);

        public void SetProviders(IEnumerable<ProviderSnapshot> providers, TimeSpan activePollInterval)
        {
            Providers = providers.ToImmutableArray();
            PollInterval = activePollInterval;
        }
    }

    private sealed class FakeOverlayWindow : FakeStatusWindow, IOverlayStatusWindow
    {
        public AppSettings? AppliedSettings { get; private set; }
        public TaskCompletionSource SettingsApplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ApplySettings(AppSettings settings)
        {
            AppliedSettings = settings;
            SettingsApplied.TrySetResult();
        }
    }

    private sealed class FakeReportSource : IStatusReportSource
    {
        private EventHandler<StatusReport>? _reportUpdated;
        public event EventHandler<bool>? RefreshStateChanged;
        public bool IsRefreshing { get; private set; }
        public int SubscriberCount { get; private set; }
        public event EventHandler<StatusReport>? ReportUpdated
        {
            add
            {
                _reportUpdated += value;
                SubscriberCount++;
            }
            remove
            {
                _reportUpdated -= value;
                SubscriberCount--;
            }
        }
        public void Raise(StatusReport report) => _reportUpdated?.Invoke(this, report);
        public void RaiseRefreshState(bool refreshing)
        {
            IsRefreshing = refreshing;
            RefreshStateChanged?.Invoke(this, refreshing);
        }
    }

    private sealed class FakeProcessLauncher : IProcessLauncher
    {
        public ProcessStartInfo? StartInfo { get; private set; }
        public void Start(ProcessStartInfo startInfo) => StartInfo = startInfo;
    }
}
