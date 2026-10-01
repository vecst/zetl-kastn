using Avalonia;
using Avalonia.Controls;

namespace ZETL;

// Tray > Measure My Taps and Holds: the measuring panel in a window of its own,
// which applies the chosen threshold and closes.
internal sealed class ZetlHoldLabWindow : Window
{
    private const int TapsWanted = 20;
    private const int HoldsWanted = 15;

    public ZetlHoldLabWindow(double currentMs, Action<int> applyThreshold, Action<string> log)
    {
        Title = "Measure my taps and holds";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        ZetlWindowPlacement.Track(this);

        Panel = new ZetlHoldMeasurePanel(
            currentMs,
            TapsWanted,
            HoldsWanted,
            forNewUser: false,
            milliseconds =>
            {
                if (milliseconds is { } chosen)
                {
                    applyThreshold(chosen);
                }

                Close();
            },
            log);

        Content = new Border
        {
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "Measure my taps and holds", Classes = { "heading" } },
                    Panel
                }
            }
        };
    }

    public ZetlHoldMeasurePanel Panel { get; }

    // For the preview: feed a press in as if the keyboard hook had timed it.
    public void RecordPress(double milliseconds) => Panel.RecordPress(milliseconds);
}
