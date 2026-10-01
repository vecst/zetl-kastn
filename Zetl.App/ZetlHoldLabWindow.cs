using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace ZETL;

// Measures how long the user's own taps and holds of Ctrl+C last, then shows
// them on a timeline against the current hold threshold and suggests one that
// separates them. While it is open the host stands Zetl down (no popups,
// captures, or indicator), so presses are measured as the hand makes them.
internal sealed class ZetlHoldLabWindow : Window
{
    private const int TapsWanted = 20;
    private const int HoldsWanted = 15;
    private const double TimelineMaxMs = 1000;
    private const double TimelineWidth = 560;
    private const double TapRowY = 18;
    private const double HoldRowY = 46;

    private readonly double currentMs;
    private readonly Action<int> applyThreshold;
    private readonly Action<string> log;
    private readonly List<double> taps = [];
    private readonly List<double> holds = [];
    private readonly TextBlock instruction = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15 };
    private readonly TextBlock progress = new() { FontWeight = FontWeight.Bold };
    private readonly TextBlock results = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Canvas timeline = new() { Width = TimelineWidth, Height = 92, ClipToBounds = false };
    private readonly Button useButton = new() { Classes = { "primary" }, IsVisible = false };
    private readonly Button keepButton = new() { IsVisible = false };
    private readonly Button againButton = new() { Content = "Measure again" };
    private bool measuringHolds;
    private int suggestedMs;

    public ZetlHoldLabWindow(double currentMs, Action<int> applyThreshold, Action<string> log)
    {
        this.currentMs = currentMs;
        this.applyThreshold = applyThreshold;
        this.log = log;
        Title = "Measure my taps and holds";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        ZetlWindowPlacement.Track(this);

        againButton.Click += (_, _) => Restart();
        keepButton.Click += (_, _) => Close();
        useButton.Click += (_, _) =>
        {
            applyThreshold(suggestedMs);
            Close();
        };

        Content = new Border
        {
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "Measure my taps and holds", Classes = { "heading" } },
                    instruction,
                    progress,
                    new Border
                    {
                        Classes = { "surface" },
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(16, 12),
                        Child = timeline
                    },
                    results,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { useButton, keepButton, againButton }
                    }
                }
            }
        };

        Restart();
    }

    // A completed press of Ctrl+C, measured by the keyboard hook from key down
    // to release.
    public void RecordPress(double milliseconds)
    {
        if (results.IsVisible)
        {
            return;
        }

        if (!measuringHolds)
        {
            taps.Add(milliseconds);
            if (taps.Count == TapsWanted)
            {
                measuringHolds = true;
            }
        }
        else
        {
            holds.Add(milliseconds);
            if (holds.Count == HoldsWanted)
            {
                ShowResults();
                return;
            }
        }

        UpdatePrompt();
        DrawTimeline();
    }

    private void Restart()
    {
        taps.Clear();
        holds.Clear();
        measuringHolds = false;
        results.IsVisible = false;
        useButton.IsVisible = false;
        keepButton.IsVisible = false;
        UpdatePrompt();
        DrawTimeline();
    }

    private void UpdatePrompt()
    {
        if (!measuringHolds)
        {
            instruction.Text = "First, your taps. Press Ctrl+C the way you always do when you copy, "
                + "20 times. Zetl is paused while this window is open, so nothing pops up.";
            progress.Text = $"Taps: {taps.Count} of {TapsWanted}";
        }
        else
        {
            instruction.Text = "Now your holds. Press Ctrl+C and keep holding it the way you would "
                + "to ask Zetl for something, then let go. 15 times.";
            progress.Text = $"Holds: {holds.Count} of {HoldsWanted}";
        }
    }

    private void ShowResults()
    {
        var result = ZetlHoldCalibration.Analyze(taps, holds, currentMs);
        suggestedMs = (int)result.SuggestedMs;
        var atCurrent = result.CurrentTapsTooLong == 0 && result.CurrentHoldsTooShort == 0
            ? $"At {currentMs:0} ms every tap stays a tap and every hold registers."
            : $"At {currentMs:0} ms, {Plural(result.CurrentTapsTooLong, "tap")} would open Zetl "
              + $"and {Plural(result.CurrentHoldsTooShort, "hold")} would be missed.";
        var suggestion = result.Separated
            ? $"Suggested: {suggestedMs} ms, halfway between your slowest tap and your quickest hold."
            : $"Your taps and holds overlap, so no threshold separates them all. "
              + $"{suggestedMs} ms misjudges the fewest presses.";

        results.Text =
            $"Taps: median {result.TapMedianMs:0} ms, slowest {result.SlowestTapMs:0} ms.\n"
            + $"Holds: quickest {result.QuickestHoldMs:0} ms, median {result.HoldMedianMs:0} ms.\n"
            + $"{atCurrent}\n{suggestion}";
        results.IsVisible = true;
        progress.Text = "Done.";
        instruction.Text = "Here is how your hands compare with the hold threshold.";
        useButton.Content = $"Use {suggestedMs} ms";
        useButton.IsVisible = suggestedMs != (int)currentMs;
        keepButton.Content = $"Keep {currentMs:0} ms";
        keepButton.IsVisible = true;
        log($"Hold lab: taps=[{Join(taps)}] holds=[{Join(holds)}] current={currentMs:0} suggested={suggestedMs}.");
        DrawTimeline(suggestedMs);
    }

    // Two rows of dots on a 0-1000 ms axis: taps above, holds below, with the
    // current threshold (and, once measured, the suggestion) as vertical lines.
    private void DrawTimeline(double? suggested = null)
    {
        timeline.Children.Clear();
        AddText("taps", -2, TapRowY - 8, muted: true);
        AddText("holds", -2, HoldRowY - 8, muted: true);
        AddLine(0, 70, TimelineWidth, 70, "ZetlBorderBrush", 1);
        foreach (var tick in new[] { 0, 250, 500, 750, 1000 })
        {
            AddText(tick == 1000 ? "1000+ ms" : $"{tick}", X(tick) - 8, 74, muted: true);
        }

        AddMarker(currentMs, $"{currentMs:0} now", "ZetlMutedTextBrush");
        if (suggested is { } value && Math.Abs(value - currentMs) >= 1)
        {
            AddMarker(value, $"{value:0}", "ZetlAccentBrush");
        }

        foreach (var ms in taps)
        {
            AddDot(ms, TapRowY, "ZetlTextBrush");
        }

        foreach (var ms in holds)
        {
            AddDot(ms, HoldRowY, "ZetlAccentBrush");
        }
    }

    private static double X(double ms) => 40 + Math.Min(ms, TimelineMaxMs) / TimelineMaxMs * (TimelineWidth - 40);

    private void AddDot(double ms, double y, string brush)
    {
        var dot = new Ellipse { Width = 9, Height = 9, Opacity = 0.75 };
        dot.Bind(Shape.FillProperty, dot.GetResourceObservable(brush));
        Canvas.SetLeft(dot, X(ms) - 4.5);
        Canvas.SetTop(dot, y - 4.5);
        timeline.Children.Add(dot);
    }

    private void AddMarker(double ms, string label, string brush)
    {
        AddLine(X(ms), 4, X(ms), 64, brush, 2);
        AddText(label, X(ms) + 4, -14, muted: brush != "ZetlAccentBrush");
    }

    private void AddLine(double x1, double y1, double x2, double y2, string brush, double thickness)
    {
        var line = new Line
        {
            StartPoint = new Point(x1, y1),
            EndPoint = new Point(x2, y2),
            StrokeThickness = thickness
        };
        line.Bind(Shape.StrokeProperty, line.GetResourceObservable(brush));
        timeline.Children.Add(line);
    }

    private void AddText(string text, double x, double y, bool muted)
    {
        var block = new TextBlock { Text = text, FontSize = 11 };
        if (muted)
        {
            block.Classes.Add("muted");
        }

        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        timeline.Children.Add(block);
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string Join(IEnumerable<double> values) =>
        string.Join(",", values.Select(value => value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
}
