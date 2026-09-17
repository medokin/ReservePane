using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ReservePane.Model;
using ReservePane.Ui;
using ReservePane.Ui.Controls;

namespace ReservePane.Tests.Ui;

[Collection(WpfStaCollection.Name)]
public sealed class UiConstructionSmokeTests
{
    [Fact]
    public void SharedCardAndWindows_ConstructOnStaThread()
    {
        // Break caught: compiled XAML or a default constructor cannot create the three shared UI surfaces.
        Exception? failure = null;
        bool? popupShowActivated = null;
        string? popupTitle = null;
        string? overlayTitle = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = new ProviderCard();
                var popup = new PopupWindow();
                var overlay = new OverlayWindow();
                popupShowActivated = popup.ShowActivated;
                popupTitle = popup.Title;
                overlayTitle = overlay.Title;
                popup.Close();
                overlay.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The STA construction thread did not finish.");

        Assert.Null(failure);
        Assert.True(popupShowActivated);
        Assert.Equal("ReservePane", popupTitle);
        Assert.Equal("ReservePane overlay", overlayTitle);
    }

    [Fact]
    public void ProviderCard_CompanySeatSnapshotRendersMemberBudgetText()
    {
        // Catches a valid provider snapshot never reaching the existing card's visible text.
        Exception? failure = null;
        string[] visibleText = [];
        var thread = new Thread(() =>
        {
            try
            {
                var card = new ProviderCard
                {
                    Snapshot = new ProviderSnapshot(
                        "opencode-company-seat",
                        "OpenCode",
                        HealthState.Ok,
                        "Company Seat",
                        [new UsageWindow(
                            "monthly budget",
                            25,
                            DateTimeOffset.UtcNow.AddDays(30),
                            Severity.Normal)],
                        [
                            new InfoLine("Spend", "USD 2.50"),
                            new InfoLine("Budget", "USD 10.00"),
                        ],
                        null,
                        DateTimeOffset.UtcNow,
                        0),
                };
                card.Measure(new Size(360, double.PositiveInfinity));
                card.Arrange(new Rect(0, 0, 360, card.DesiredSize.Height));
                card.UpdateLayout();
                visibleText = Descendants<TextBlock>(card)
                    .Select(textBlock => new TextRange(
                        textBlock.ContentStart,
                        textBlock.ContentEnd).Text.Trim())
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The STA rendering thread did not finish.");

        Assert.Null(failure);
        Assert.Contains("OpenCode", visibleText);
        Assert.Contains("Company Seat", visibleText);
        Assert.Contains("monthly budget", visibleText);
        Assert.Contains("25%", visibleText);
        Assert.Contains(visibleText, text => text.StartsWith("resets ", StringComparison.Ordinal));
        Assert.Contains("Spend", visibleText);
        Assert.Contains("USD 2.50", visibleText);
        Assert.Contains("Budget", visibleText);
        Assert.Contains("USD 10.00", visibleText);
        Assert.DoesNotContain(visibleText, text => text.Contains("organization", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("ollama", "Ollama Cloud", "Max", "Estimated spend", "USD 112.50", "USD 300.00")]
    [InlineData("claude", "Claude", "team_standard", "Spend", "EUR 123.45", "EUR 1000.00")]
    [InlineData("claude", "Claude", "team_standard", "Spend", "EUR 322.52", "no cap set")]
    public void ProviderCard_SpendAndBudgetRenderAtPopupWidth(
        string id, string label, string plan, string spendLabel, string spend, string budget)
    {
        // Catches missing budget metadata or overlapping labels and values at the narrow card width.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var card = new ProviderCard
                {
                    Snapshot = new ProviderSnapshot(id, label, HealthState.Ok, plan,
                        [new UsageWindow("Monthly", 37.5, null, Severity.Normal)],
                        [new InfoLine(spendLabel, spend), new InfoLine("Budget", budget)],
                        null, DateTimeOffset.UtcNow, 0),
                };
                card.Measure(new Size(327, double.PositiveInfinity));
                card.Arrange(new Rect(0, 0, 327, card.DesiredSize.Height));
                card.UpdateLayout();

                TextBlock[] blocks = Descendants<TextBlock>(card).ToArray();
                Rect cardBounds = new(0, 0, 327, card.ActualHeight);
                foreach ((string rowLabel, string value) in new[]
                    {
                        (label, plan),
                        ("Monthly", "37.5%"),
                        (spendLabel, spend),
                        ("Budget", budget),
                    })
                {
                    TextBlock labelBlock = Assert.Single(blocks, block => block.Text == rowLabel);
                    TextBlock valueBlock = Assert.Single(blocks, block => block.Text == value);
                    Assert.Equal(Visibility.Visible, labelBlock.Visibility);
                    Assert.Equal(Visibility.Visible, valueBlock.Visibility);
                    Rect labelBounds = labelBlock.TransformToAncestor(card)
                        .TransformBounds(new Rect(new Point(), labelBlock.RenderSize));
                    Rect valueBounds = valueBlock.TransformToAncestor(card)
                        .TransformBounds(new Rect(new Point(), valueBlock.RenderSize));
                    Assert.True(cardBounds.Contains(labelBounds), $"The {rowLabel} label extends outside the card.");
                    Assert.True(cardBounds.Contains(valueBounds), $"The {value} value extends outside the card.");
                    Assert.True(labelBounds.Right < valueBounds.Left, $"The {rowLabel} label overlaps its value.");
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The STA budget rendering thread did not finish.");
        Assert.Null(failure);
    }

    [Fact]
    public void ProviderCard_UnknownUsageIsExplicitAndRecoversWhenRefreshed()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var card = new ProviderCard();
                foreach ((double? percent, string expectedText) in new (double?, string)[]
                    { (null, "Unavailable"), (0, "0%"), (25.5, "25.5%"), (null, "Unavailable") })
                {
                    card.Snapshot = new ProviderSnapshot("sample", "Sample", HealthState.Ok, null,
                        [new UsageWindow("Monthly", percent, null, Severity.Normal)], [], null,
                        DateTimeOffset.UtcNow, 0);
                    card.Measure(new Size(327, double.PositiveInfinity));
                    card.Arrange(new Rect(0, 0, 327, card.DesiredSize.Height));
                    card.UpdateLayout();

                    string[] text = Descendants<TextBlock>(card).Select(block => block.Text).ToArray();
                    Assert.Contains(expectedText, text);
                    Assert.DoesNotContain("%", text);
                    Border runway = Assert.Single(Descendants<Border>(card),
                        border => border.Height == 6 && border.DataContext is UsageWindow);
                    Assert.Equal(percent is null ? Visibility.Collapsed : Visibility.Visible, runway.Visibility);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The STA rendering thread did not finish.");
        Assert.Null(failure);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
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
