using System.Reflection;
using System.Windows;

namespace ReservePane.Ui.Controls;

public partial class StatusWindowHeader : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty IsRefreshingProperty = DependencyProperty.Register(
        nameof(IsRefreshing),
        typeof(bool),
        typeof(StatusWindowHeader),
        new PropertyMetadata(false));

    public StatusWindowHeader()
    {
        InitializeComponent();
    }

    public event EventHandler? RefreshRequested;
    public event EventHandler? CloseRequested;

    public bool IsRefreshing
    {
        get => (bool)GetValue(IsRefreshingProperty);
        set => SetValue(IsRefreshingProperty, value);
    }

    public string VersionText { get; } = "v" + (typeof(StatusWindowHeader).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(StatusWindowHeader).Assembly.GetName().Version?.ToString(3)
        ?? "unknown");

    private void OnRefreshClicked(object sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (!IsRefreshing)
        {
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs args)
    {
        args.Handled = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
