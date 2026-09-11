using System.Globalization;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Threading;
using ReservePane.Core;
using ReservePane.Model;
using ReservePane.Platform;
using ReservePane.Providers;
using Forms = System.Windows.Forms;

namespace ReservePane.Ui;

public static class TrayTooltip
{
    public const int MaximumLength = 127;

    public static string Format(StatusReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Providers.IsEmpty)
        {
            return string.Empty;
        }

        TooltipLine[] lines = report.Providers
            .Select(CreateLine)
            .ToArray();
        string full = string.Join('\n', lines.Select(static line => line.Label + line.Suffix));
        if (full.Length <= MaximumLength)
        {
            return full;
        }

        int availableLabelLength = MaximumLength
            - (lines.Length - 1)
            - lines.Sum(static line => line.Suffix.Length);
        int[] allocations = lines
            .Select(static line => line.Label.Length)
            .ToArray();
        int minimumTotal = lines.Sum(static line => Math.Min(line.Label.Length, 3));
        availableLabelLength = Math.Max(minimumTotal, availableLabelLength);

        while (allocations.Sum() > availableLabelLength)
        {
            int index = Enumerable.Range(0, allocations.Length)
                .Where(candidate => allocations[candidate] > Math.Min(lines[candidate].Label.Length, 3))
                .MaxBy(candidate => allocations[candidate]);
            allocations[index]--;
        }

        return string.Join(
            '\n',
            lines.Select((line, index) =>
                TruncateLabel(line.Label, allocations[index]) + line.Suffix));
    }

    private static TooltipLine CreateLine(ProviderSnapshot provider)
    {
        string label = provider.Label.Replace('\r', ' ').Replace('\n', ' ');
        double[] percentages = provider.Windows
            .Where(static window => window.Percent is double percent && double.IsFinite(percent))
            .Select(static window => window.Percent!.Value)
            .ToArray();
        string suffix = percentages.Length == 0
            ? string.Empty
            : " " + FormatPercentage(percentages.Max()) + "%";
        return new TooltipLine(label, suffix);
    }

    private static string FormatPercentage(double percent) =>
        Math.Abs(percent) >= 1_000_000
            ? percent.ToString("0.###E+0", CultureInfo.InvariantCulture)
            : percent.ToString("0.##", CultureInfo.InvariantCulture);

    private static string TruncateLabel(string label, int allocation)
    {
        if (label.Length <= allocation)
        {
            return label;
        }

        int contentLength = Math.Max(0, allocation - 3);
        var builder = new StringBuilder(allocation);
        int used = 0;
        foreach (Rune rune in label.EnumerateRunes())
        {
            if (used + rune.Utf16SequenceLength > contentLength)
            {
                break;
            }

            builder.Append(rune.ToString());
            used += rune.Utf16SequenceLength;
        }

        return builder.Append("...").ToString();
    }

    private sealed record TooltipLine(string Label, string Suffix);
}

internal enum TrayCommand
{
    ToggleOverlay,
    ToggleAutostart,
    Refresh,
    OpenSettings,
    Exit,
}

internal sealed record TrayMenuItemDefinition(
    TrayCommand Command,
    string Text,
    bool Checkable);

internal static class TrayMenuDefinition
{
    internal static IReadOnlyList<TrayMenuItemDefinition> Items { get; } =
    [
        new(TrayCommand.ToggleOverlay, "Show corner overlay", true),
        new(TrayCommand.ToggleAutostart, "Start with Windows", true),
        new(TrayCommand.Refresh, "Refresh now", false),
        new(TrayCommand.OpenSettings, "Settings file", false),
        new(TrayCommand.Exit, "Exit", false),
    ];
}

internal interface ITrayIconView : IDisposable
{
    event Action? LeftClicked;
    event Action? MenuOpening;
    event Action<TrayCommand>? CommandInvoked;

    bool Visible { get; set; }
    bool OverlayChecked { get; set; }
    bool AutostartChecked { get; set; }
    TrayState State { get; set; }
    string Tooltip { get; set; }
}

internal interface IStatusWindow
{
    event EventHandler? RefreshRequested;
    event EventHandler? CloseRequested;
    bool IsVisible { get; }
    void Show();
    void Hide();
    void SetProviders(IEnumerable<ProviderSnapshot> providers, TimeSpan activePollInterval);
    void SetRefreshing(bool refreshing);
}

internal interface IOverlayStatusWindow : IStatusWindow
{
    void ApplySettings(AppSettings settings);
}

internal interface IStatusReportSource
{
    event EventHandler<StatusReport>? ReportUpdated;
    event EventHandler<bool>? RefreshStateChanged;
    bool IsRefreshing { get; }
}

internal interface IProcessLauncher
{
    void Start(ProcessStartInfo startInfo);
}

internal sealed class SettingsFileLauncher(
    SettingsStore settingsStore,
    string settingsPath,
    IProcessLauncher processLauncher)
{
    private readonly SettingsStore _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    private readonly string _settingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
    private readonly IProcessLauncher _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        await _settingsStore.UpdateAsync(static settings => settings, cancellationToken);
        _processLauncher.Start(new ProcessStartInfo(_settingsPath)
        {
            UseShellExecute = true,
        });
    }
}

internal sealed class ShellProcessLauncher : IProcessLauncher
{
    public void Start(ProcessStartInfo startInfo) => _ = Process.Start(startInfo);
}

internal sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    private readonly Dispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public void Post(Action action) =>
        _ = _dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);

    public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action).Task;
}

internal sealed class StatusPollerReportSource(StatusPoller poller) : IStatusReportSource
{
    private readonly StatusPoller _poller = poller ?? throw new ArgumentNullException(nameof(poller));

    public event EventHandler<StatusReport>? ReportUpdated
    {
        add => _poller.ReportUpdated += value;
        remove => _poller.ReportUpdated -= value;
    }

    public bool IsRefreshing => _poller.IsRefreshing;

    public event EventHandler<bool>? RefreshStateChanged
    {
        add => _poller.RefreshStateChanged += value;
        remove => _poller.RefreshStateChanged -= value;
    }
}

internal sealed class WinFormsTrayIconView : ITrayIconView
{
    private readonly TrayIconRenderer _renderer = new();
    private readonly Forms.NotifyIcon _notifyIcon = new();
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Dictionary<TrayCommand, Forms.ToolStripMenuItem> _items = [];
    private bool _disposed;
    private TrayState _state;

    public WinFormsTrayIconView()
    {
        foreach (TrayMenuItemDefinition definition in TrayMenuDefinition.Items)
        {
            var item = new Forms.ToolStripMenuItem(definition.Text)
            {
                CheckOnClick = false,
                CheckState = Forms.CheckState.Unchecked,
                Tag = definition.Command,
            };
            item.Click += OnItemClicked;
            _items.Add(definition.Command, item);
            _menu.Items.Add(item);
        }

        _menu.Opening += OnMenuOpening;
        _notifyIcon.ContextMenuStrip = _menu;
        _notifyIcon.MouseClick += OnMouseClick;
        _notifyIcon.Text = "ReservePane";
        State = TrayState.Grey;
    }

    public event Action? LeftClicked;
    public event Action? MenuOpening;
    public event Action<TrayCommand>? CommandInvoked;

    public bool Visible
    {
        get => _notifyIcon.Visible;
        set => _notifyIcon.Visible = value;
    }

    public bool OverlayChecked
    {
        get => _items[TrayCommand.ToggleOverlay].Checked;
        set => _items[TrayCommand.ToggleOverlay].Checked = value;
    }

    public bool AutostartChecked
    {
        get => _items[TrayCommand.ToggleAutostart].Checked;
        set => _items[TrayCommand.ToggleAutostart].Checked = value;
    }

    public TrayState State
    {
        get => _state;
        set
        {
            Icon icon = _renderer.Create(value);
            _notifyIcon.Icon = icon;
            _state = value;
        }
    }

    public string Tooltip
    {
        get => _notifyIcon.Text;
        set => _notifyIcon.Text = value;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.MouseClick -= OnMouseClick;
        _menu.Opening -= OnMenuOpening;
        foreach (Forms.ToolStripMenuItem item in _items.Values)
        {
            item.Click -= OnItemClicked;
        }

        _notifyIcon.Dispose();
        _menu.Dispose();
        _renderer.Dispose();
    }

    private void OnMouseClick(object? sender, Forms.MouseEventArgs args)
    {
        if (args.Button == Forms.MouseButtons.Left)
        {
            LeftClicked?.Invoke();
        }
    }

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs args) =>
        MenuOpening?.Invoke();

    private void OnItemClicked(object? sender, EventArgs args)
    {
        if (sender is Forms.ToolStripMenuItem { Tag: TrayCommand command })
        {
            CommandInvoked?.Invoke(command);
        }
    }
}

public sealed class TrayIconHost : IDisposable
{
    private readonly object _gate = new();
    private readonly ITrayIconView _view;
    private readonly IUiDispatcher _dispatcher;
    private readonly IStatusWindow _popup;
    private readonly IOverlayStatusWindow _overlay;
    private readonly IStatusReportSource _reports;
    private readonly ThresholdWatcher _watcher;
    private readonly Func<AppSettings> _settings;
    private readonly Func<TimeSpan> _activePollInterval;
    private readonly SettingsUpdate _updateSettings;
    private readonly Func<bool> _autostartEnabled;
    private readonly Action<bool> _setAutostart;
    private readonly Action _requestRefresh;
    private readonly Func<Task> _openSettings;
    private readonly Func<Task> _beginShutdown;
    private readonly Action<StatusAlert> _showToast;
    private readonly RollingFileLog _log;
    private StatusReport? _previousReport;
    private bool _disposed;

    public TrayIconHost(
        Dispatcher dispatcher,
        PopupWindow popup,
        OverlayWindow overlay,
        StatusPoller poller,
        ThresholdWatcher watcher,
        Func<AppSettings> settings,
        Func<TimeSpan> activePollInterval,
        SettingsStore settingsStore,
        AutostartService autostart,
        ToastNotifier toast,
        string settingsPath,
        Func<Task> beginShutdown,
        RollingFileLog log)
        : this(
            new WinFormsTrayIconView(),
            new WpfUiDispatcher(dispatcher),
            popup,
            overlay,
            new StatusPollerReportSource(poller),
            watcher,
            settings,
            activePollInterval,
            settingsStore.UpdateAsync,
            () => autostart.IsEnabled,
            autostart.SetEnabled,
            poller.RequestRefresh,
            () => new SettingsFileLauncher(settingsStore, settingsPath, new ShellProcessLauncher())
                .OpenAsync(CancellationToken.None),
            beginShutdown,
            toast.Show,
            log)
    {
    }

    internal TrayIconHost(
        ITrayIconView view,
        IUiDispatcher dispatcher,
        IStatusWindow popup,
        IOverlayStatusWindow overlay,
        IStatusReportSource reports,
        ThresholdWatcher watcher,
        Func<AppSettings> settings,
        Func<TimeSpan> activePollInterval,
        SettingsUpdate updateSettings,
        Func<bool> autostartEnabled,
        Action<bool> setAutostart,
        Action requestRefresh,
        Func<Task> openSettings,
        Func<Task> beginShutdown,
        Action<StatusAlert> showToast,
        RollingFileLog log)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _popup = popup ?? throw new ArgumentNullException(nameof(popup));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _activePollInterval = activePollInterval ?? throw new ArgumentNullException(nameof(activePollInterval));
        _updateSettings = updateSettings ?? throw new ArgumentNullException(nameof(updateSettings));
        _autostartEnabled = autostartEnabled ?? throw new ArgumentNullException(nameof(autostartEnabled));
        _setAutostart = setAutostart ?? throw new ArgumentNullException(nameof(setAutostart));
        _requestRefresh = requestRefresh ?? throw new ArgumentNullException(nameof(requestRefresh));
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _beginShutdown = beginShutdown ?? throw new ArgumentNullException(nameof(beginShutdown));
        _showToast = showToast ?? throw new ArgumentNullException(nameof(showToast));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        try
        {
            _view.LeftClicked += OnLeftClicked;
            _view.MenuOpening += OnMenuOpening;
            _view.CommandInvoked += OnCommandInvoked;
            _reports.ReportUpdated += OnReportUpdated;
            _reports.RefreshStateChanged += OnRefreshStateChanged;
            _popup.RefreshRequested += OnWindowRefreshRequested;
            _overlay.RefreshRequested += OnWindowRefreshRequested;
            _popup.CloseRequested += OnPopupCloseRequested;
            _overlay.CloseRequested += OnOverlayCloseRequested;
            _popup.SetRefreshing(_reports.IsRefreshing);
            _overlay.SetRefreshing(_reports.IsRefreshing);
            _view.Visible = true;
        }
        catch
        {
            RollBackConstruction();
            throw;
        }
    }

    internal async Task ExecuteCommandAsync(TrayCommand command)
    {
        ThrowIfDisposed();
        switch (command)
        {
            case TrayCommand.ToggleOverlay:
                await ToggleOverlayAsync();
                break;
            case TrayCommand.ToggleAutostart:
                await ToggleAutostartAsync();
                break;
            case TrayCommand.Refresh:
                _requestRefresh();
                break;
            case TrayCommand.OpenSettings:
                await _openSettings();
                break;
            case TrayCommand.Exit:
                await _beginShutdown();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    public async Task ToggleOverlayAsync()
    {
        ThrowIfDisposed();
        await SetOverlayVisibleAsync(!_overlay.IsVisible);
    }

    private async Task SetOverlayVisibleAsync(bool visible)
    {
        if (visible)
        {
            _overlay.Show();
        }
        else
        {
            _overlay.Hide();
        }

        AppSettings updated = await _updateSettings(
            settings => settings with { OverlayVisible = visible },
            CancellationToken.None);
        _overlay.ApplySettings(updated);
        _view.OverlayChecked = visible;
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

        _reports.ReportUpdated -= OnReportUpdated;
        UnsubscribeWindowControls();
        _view.LeftClicked -= OnLeftClicked;
        _view.MenuOpening -= OnMenuOpening;
        _view.CommandInvoked -= OnCommandInvoked;
        _view.Visible = false;
        _view.Dispose();
    }

    private void OnLeftClicked()
    {
        if (IsDisposed())
        {
            return;
        }

        if (_popup.IsVisible)
        {
            _popup.Hide();
        }
        else
        {
            _popup.Show();
        }
    }

    private void OnMenuOpening()
    {
        if (IsDisposed())
        {
            return;
        }

        try
        {
            _view.OverlayChecked = _overlay.IsVisible;
            _view.AutostartChecked = _autostartEnabled();
        }
        catch (Exception exception)
        {
            TryLogFailure(exception);
        }
    }

    private void OnCommandInvoked(TrayCommand command) =>
        _ = ExecuteCommandSafelyAsync(command);

    private void OnWindowRefreshRequested(object? sender, EventArgs args) =>
        OnCommandInvoked(TrayCommand.Refresh);

    private void OnPopupCloseRequested(object? sender, EventArgs args)
    {
        if (!IsDisposed())
        {
            _popup.Hide();
        }
    }

    private async void OnOverlayCloseRequested(object? sender, EventArgs args)
    {
        if (IsDisposed())
        {
            return;
        }

        try
        {
            await SetOverlayVisibleAsync(false);
        }
        catch (Exception exception)
        {
            TryLogFailure(exception);
        }
    }

    private void OnRefreshStateChanged(object? sender, bool refreshing)
    {
        if (IsDisposed())
        {
            return;
        }

        try
        {
            _dispatcher.Post(() =>
            {
                if (IsDisposed())
                {
                    return;
                }

                try
                {
                    _popup.SetRefreshing(refreshing);
                    _overlay.SetRefreshing(refreshing);
                }
                catch (Exception exception)
                {
                    TryLogFailure(exception);
                }
            });
        }
        catch (Exception exception)
        {
            TryLogFailure(exception);
        }
    }

    private async Task ExecuteCommandSafelyAsync(TrayCommand command)
    {
        try
        {
            await ExecuteCommandAsync(command);
        }
        catch (ObjectDisposedException) when (IsDisposed())
        {
        }
        catch (Exception exception)
        {
            TryLogFailure(exception);
        }
    }

    private async Task ToggleAutostartAsync()
    {
        bool enabled = !_autostartEnabled();
        _setAutostart(enabled);
        await _updateSettings(
            settings => settings with { Autostart = enabled },
            CancellationToken.None);
        _view.AutostartChecked = _autostartEnabled();
    }

    private void OnReportUpdated(object? sender, StatusReport report)
    {
        if (IsDisposed())
        {
            return;
        }

        try
        {
            _dispatcher.Post(() => DeliverReportSafely(report));
        }
        catch (Exception exception)
        {
            TryLogFailure(exception);
        }
    }

    private void DeliverReportSafely(StatusReport report)
    {
        if (IsDisposed())
        {
            return;
        }

        try
        {
            AppSettings settings = _settings();
            TimeSpan interval = _activePollInterval();
            _popup.SetProviders(report.Providers, interval);
            _overlay.SetProviders(report.Providers, interval);
            _view.State = TrayStatusPolicy.GetState(
                report,
                settings.WarningPercent,
                settings.CriticalPercent);
            _view.Tooltip = TrayTooltip.Format(report);

            foreach (StatusAlert alert in _watcher.Evaluate(_previousReport, report))
            {
                try
                {
                    _showToast(alert);
                }
                catch (Exception exception)
                {
                    TryLogFailure(exception);
                }
            }

            _previousReport = report;
        }
        catch (Exception exception)
        {
            TryLogFailure(exception);
        }
    }

    private bool IsDisposed()
    {
        lock (_gate)
        {
            return _disposed;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed(), this);

    private void RollBackConstruction()
    {
        try
        {
            _reports.ReportUpdated -= OnReportUpdated;
            UnsubscribeWindowControls();
            _view.LeftClicked -= OnLeftClicked;
            _view.MenuOpening -= OnMenuOpening;
            _view.CommandInvoked -= OnCommandInvoked;
        }
        catch (Exception)
        {
        }

        try
        {
            _view.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private void TryLogFailure(Exception exception)
    {
        try
        {
            _log.Write(LogArea.Ui, LogOutcome.Failed, exception: exception);
        }
        catch (Exception)
        {
        }
    }

    private void UnsubscribeWindowControls()
    {
        _reports.RefreshStateChanged -= OnRefreshStateChanged;
        _popup.RefreshRequested -= OnWindowRefreshRequested;
        _overlay.RefreshRequested -= OnWindowRefreshRequested;
        _popup.CloseRequested -= OnPopupCloseRequested;
        _overlay.CloseRequested -= OnOverlayCloseRequested;
    }
}
