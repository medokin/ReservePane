using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ReservePane.Core;
using ReservePane.Model;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace ReservePane.Ui;

public partial class OverlayWindow : Window, IOverlayStatusWindow
{
    private readonly SettingsStore? _settingsStore;
    private readonly WindowPlacementService _placementService;
    private readonly OverlayPositionPersistence? _positionPersistence;
    private readonly OverlayDragState _dragState;
    private readonly IOverlayExtendedStyle _extendedStyle;
    private Point _dragOffset;

    public OverlayWindow()
        : this(null, new WindowPlacementService())
    {
    }

    public OverlayWindow(SettingsStore settingsStore)
        : this(settingsStore, new WindowPlacementService())
    {
    }

    internal OverlayWindow(SettingsStore? settingsStore, WindowPlacementService placementService)
        : this(settingsStore, placementService, new OverlayDragState())
    {
    }

    internal OverlayWindow(
        SettingsStore? settingsStore,
        WindowPlacementService placementService,
        OverlayDragState dragState)
        : this(settingsStore, placementService, dragState, new OverlayExtendedStyle())
    {
    }

    internal OverlayWindow(
        SettingsStore? settingsStore,
        WindowPlacementService placementService,
        OverlayDragState dragState,
        IOverlayExtendedStyle extendedStyle)
    {
        _settingsStore = settingsStore;
        _placementService = placementService ?? throw new ArgumentNullException(nameof(placementService));
        _dragState = dragState ?? throw new ArgumentNullException(nameof(dragState));
        _extendedStyle = extendedStyle ?? throw new ArgumentNullException(nameof(extendedStyle));
        if (settingsStore is not null)
        {
            _positionPersistence = new OverlayPositionPersistence(settingsStore);
            _positionPersistence.Failed += OnPositionPersistenceFailed;
        }

        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += OnIsVisibleChanged;
        LostMouseCapture += OnLostMouseCapture;
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

    internal bool IsDragging => _dragState.IsDragging;

    public Exception? LastPositionPersistenceFailure { get; private set; }

    public event Action<object?, Exception>? PositionPersistenceFailed;

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

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (IsVisible)
        {
            ApplyConfiguredPlacement(settings);
        }
    }

    internal Task FlushPositionPersistenceAsync(CancellationToken cancellationToken = default) =>
        _positionPersistence?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

    protected override void OnClosed(EventArgs args)
    {
        SourceInitialized -= OnSourceInitialized;
        IsVisibleChanged -= OnIsVisibleChanged;
        LostMouseCapture -= OnLostMouseCapture;
        if (_positionPersistence is not null)
        {
            _positionPersistence.Failed -= OnPositionPersistenceFailed;
        }

        base.OnClosed(args);
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        try
        {
            _extendedStyle.Apply(handle);
        }
        catch
        {
            IsHitTestVisible = false;
            ShowActivated = false;
            throw;
        }
    }

    private async void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!IsVisible)
        {
            return;
        }

        try
        {
            await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            AppSettings settings = _settingsStore is null
                ? AppSettings.Default
                : await _settingsStore.LoadAsync(CancellationToken.None);
            ApplyConfiguredPlacement(settings);
        }
        catch (ObjectDisposedException exception)
        {
            OnPositionPersistenceFailed(this, exception);
        }
        catch (IOException exception)
        {
            OnPositionPersistenceFailed(this, exception);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            OnPositionPersistenceFailed(this, exception);
        }
    }

    private void ApplyConfiguredPlacement(AppSettings settings)
    {
        IReadOnlyList<MonitorWorkArea> monitors = _placementService.GetMonitorWorkAreas();
        if (monitors.Count == 0)
        {
            return;
        }

        Size size = new(Math.Max(ActualWidth, MinWidth), Math.Max(ActualHeight, 1));
        MonitorWorkArea monitor = WindowPlacementService.ResolveConfiguredMonitor(settings.OverlayMonitorId, monitors);
        Point position = WindowPlacementService.GetOverlayPosition(settings, monitors, size);
        CustomOverlayPosition clamped = WindowPlacementService.ClampCustomPosition(position, size, monitor);
        _placementService.PositionWindow(this, clamped.Position);
    }

    private void OnDragStarted(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            Point cursor = PointToScreen(args.GetPosition(this));
            Rect bounds = _placementService.GetWindowBounds(this);
            _dragOffset = new Point(cursor.X - bounds.Left, cursor.Y - bounds.Top);
            _dragState.Begin(CaptureMouse());
            args.Handled = _dragState.IsDragging;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            _dragState.Cancel();
            OnPositionPersistenceFailed(this, exception);
        }
    }

    private void OnDragMoved(object sender, MouseEventArgs args)
    {
        if (!_dragState.IsDragging)
        {
            return;
        }

        if (args.LeftButton != MouseButtonState.Pressed)
        {
            CancelDrag();
            return;
        }

        try
        {
            Point cursor = PointToScreen(args.GetPosition(this));
            Point desired = new(cursor.X - _dragOffset.X, cursor.Y - _dragOffset.Y);
            IReadOnlyList<MonitorWorkArea> monitors = _placementService.GetMonitorWorkAreas();
            if (monitors.Count == 0)
            {
                CancelDrag();
                return;
            }

            MonitorWorkArea monitor = WindowPlacementService.FindNearestMonitor(cursor, monitors);
            CustomOverlayPosition clamped = WindowPlacementService.ClampCustomPosition(desired, GetDesiredSize(), monitor);
            _placementService.PositionWindow(this, clamped.Position);
            args.Handled = true;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            CancelDrag();
            OnPositionPersistenceFailed(this, exception);
        }
    }

    private void OnDragEnded(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left || !_dragState.End())
        {
            return;
        }

        ReleaseMouseCapture();
        args.Handled = true;

        if (_positionPersistence is null)
        {
            return;
        }

        try
        {
            CustomOverlayPosition? clamped = ClampCurrentPosition();
            if (clamped is null)
            {
                return;
            }

            _ = _positionPersistence.QueueAsync(clamped);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            OnPositionPersistenceFailed(this, exception);
        }
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs args)
    {
        _dragState.Cancel();
    }

    private void CancelDrag()
    {
        if (!_dragState.IsDragging)
        {
            return;
        }

        _dragState.Cancel();
        ReleaseMouseCapture();
    }

    private CustomOverlayPosition? ClampCurrentPosition()
    {
        IReadOnlyList<MonitorWorkArea> monitors = _placementService.GetMonitorWorkAreas();
        if (monitors.Count == 0)
        {
            return null;
        }

        Rect bounds = _placementService.GetWindowBounds(this);
        Point center = new(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));
        MonitorWorkArea monitor = WindowPlacementService.FindNearestMonitor(center, monitors);
        CustomOverlayPosition clamped = WindowPlacementService.ClampCustomPosition(bounds.TopLeft, GetDesiredSize(), monitor);
        _placementService.PositionWindow(this, clamped.Position);
        return clamped;
    }

    private Size GetDesiredSize() => new(Math.Max(ActualWidth, MinWidth), Math.Max(ActualHeight, 1));

    private void OnPositionPersistenceFailed(object? sender, Exception exception)
    {
        LastPositionPersistenceFailure = exception;
        Action<object?, Exception>? handlers = PositionPersistenceFailed;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<object?, Exception> handler in handlers.GetInvocationList().Cast<Action<object?, Exception>>())
        {
            try
            {
                handler(this, exception);
            }
            catch (Exception)
            {
            }
        }
    }

}
