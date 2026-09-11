using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ReservePane.Ui;

namespace ReservePane.Tests.Ui;

[Collection(WpfStaCollection.Name)]
public sealed class WindowHeaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeaderButtons_RouteRequestsAndDisableRefreshWhileBusy(bool overlay)
    {
        RunOnSta(() =>
        {
            Window window = overlay ? new OverlayWindow() : new PopupWindow();
            try
            {
                var statusWindow = (IStatusWindow)window;
                int refreshRequests = 0;
                int closeRequests = 0;
                statusWindow.RefreshRequested += (_, _) => refreshRequests++;
                statusWindow.CloseRequested += (_, _) => closeRequests++;
                FrameworkElement content = (FrameworkElement)window.Content;
                content.Measure(new Size(340, double.PositiveInfinity));
                content.Arrange(new Rect(0, 0, 340, content.DesiredSize.Height));
                content.UpdateLayout();
                Button refresh = Assert.Single(Descendants<Button>(content),
                    button => AutomationProperties.GetName(button) == "Refresh now");
                Button close = Assert.Single(Descendants<Button>(content),
                    button => AutomationProperties.GetName(button) == "Close to tray");

                refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, refreshRequests);
                statusWindow.SetRefreshing(true);
                Assert.False(refresh.IsEnabled);
                Assert.Contains("Refreshing", refresh.Content.ToString(), StringComparison.Ordinal);
                Assert.True(close.IsEnabled);
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, closeRequests);

                statusWindow.SetRefreshing(false);
                Assert.True(refresh.IsEnabled);
                Assert.Contains(Descendants<TextBlock>(content),
                    text => text.Text.StartsWith("v", StringComparison.Ordinal)
                        && Version.TryParse(text.Text[1..].Split('-')[0], out _));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlayHeaderButtonMouseDown_DoesNotStartDragging(bool refreshing)
    {
        RunOnSta(() =>
        {
            var drag = new OverlayDragState();
            var window = new OverlayWindow(null, new WindowPlacementService(), drag);
            try
            {
                window.Show();
                FrameworkElement content = (FrameworkElement)window.Content;
                content.Measure(new Size(340, double.PositiveInfinity));
                content.Arrange(new Rect(0, 0, 340, content.DesiredSize.Height));
                content.UpdateLayout();
                Button refresh = Assert.Single(Descendants<Button>(content),
                    button => AutomationProperties.GetName(button) == "Refresh now");
                window.SetRefreshing(refreshing);
                content.UpdateLayout();
                var inputTarget = Assert.IsAssignableFrom<UIElement>(
                    content.InputHitTest(refresh.TranslatePoint(new Point(5, 5), content)));
                inputTarget.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                });

                Assert.False(window.IsDragging);
                Assert.Null(window.LastPositionPersistenceFailure);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The STA header test did not finish.");
        Assert.Null(failure);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
