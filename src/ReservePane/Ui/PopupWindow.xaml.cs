using System.Collections.ObjectModel;
using System.Windows;
using ReservePane.Model;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace ReservePane.Ui;

public partial class PopupWindow : Window, IStatusWindow
{
    private readonly IPopupPlacementService _placementService;
    private readonly IPopupPositionScheduler _positionScheduler;
    private IPopupPositionOperation? _pendingPosition;

    public PopupWindow()
        : this(new WindowPlacementService(), null)
    {
    }

    internal PopupWindow(
        IPopupPlacementService placementService,
        IPopupPositionScheduler? positionScheduler = null)
    {
        _placementService = placementService ?? throw new ArgumentNullException(nameof(placementService));
        _positionScheduler = positionScheduler ?? new DispatcherPopupPositionScheduler(Dispatcher);
        InitializeComponent();
        Deactivated += OnDeactivated;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public ObservableCollection<ProviderSnapshot> Providers { get; } = [];

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    public event EventHandler? RefreshRequested
    {
        add => Header.RefreshRequested += value;
        remove => Header.RefreshRequested -= value;
    }

    public event EventHandler? CloseRequested
    {
        add => Header.CloseRequested += value;
        remove => Header.CloseRequested -= value;
    }

    public void SetRefreshing(bool refreshing) => Header.IsRefreshing = refreshing;

    public void SetProviders(IEnumerable<ProviderSnapshot> providers, TimeSpan activePollInterval)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (activePollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(activePollInterval));
        }

        PollInterval = activePollInterval;
        Providers.Clear();
        foreach (ProviderSnapshot provider in providers)
        {
            Providers.Add(provider);
        }
    }

    protected override void OnClosed(EventArgs args)
    {
        CancelPendingPosition();
        Deactivated -= OnDeactivated;
        IsVisibleChanged -= OnIsVisibleChanged;
        base.OnClosed(args);
    }

    private void OnDeactivated(object? sender, EventArgs args) => Hide();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        CancelPendingPosition();
        if (IsVisible)
        {
            _pendingPosition = _positionScheduler.Schedule(() =>
            {
                _pendingPosition = null;
                PositionNearNotificationArea();
            });
        }
    }

    private void CancelPendingPosition()
    {
        IPopupPositionOperation? pending = _pendingPosition;
        _pendingPosition = null;
        pending?.Cancel();
    }

    private void PositionNearNotificationArea()
    {
        Size size = new(Math.Max(ActualWidth, MinWidth), Math.Max(ActualHeight, 1));
        (MonitorWorkArea Monitor, Rect Area, TaskbarEdge Edge)? notification = _placementService.GetNotificationArea();
        if (notification is not null)
        {
            Point position = WindowPlacementService.GetPopupPosition(
                notification.Value.Monitor,
                notification.Value.Area,
                size,
                notification.Value.Edge);
            _placementService.PositionWindow(this, position);
            return;
        }

        IReadOnlyList<MonitorWorkArea> monitors = _placementService.GetMonitorWorkAreas();
        if (monitors.Count == 0)
        {
            return;
        }

        MonitorWorkArea primary = WindowPlacementService.ResolveConfiguredMonitor(null, monitors);
        Rect fallbackAnchor = new(primary.WorkingArea.Right, primary.WorkingArea.Bottom, 0, 0);
        Point fallback = WindowPlacementService.GetPopupPosition(primary, fallbackAnchor, size, TaskbarEdge.Bottom);
        _placementService.PositionWindow(this, fallback);
    }
}
