using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace ZETL;

// Receives the length of each plain Ctrl+C press, key down to release, while
// the host has Zetl stood down for measuring.
internal interface IZetlPressMeasurer
{
    void RecordPress(double milliseconds);
}

// Measures how long the user's own taps and holds of Ctrl+C last, then shows
// them on a timeline against the current hold threshold and suggests one that
// separates them. Used by the hold lab and by the tutorial's first step; while
// it measures, the host stands Zetl down (no popups, captures, or indicator),
// so presses are measured as the hand makes them.
internal sealed class ZetlHoldMeasurePanel : UserControl, IZetlPressMeasurer
{
    private const double TimelineMaxMs = 1000;
    private const double TimelineWidth = 560;
    private const double TapRowY = 18;
    private const double HoldRowY = 46;

    private readonly double currentMs;
    private readonly int tapsWanted;
    private readonly int holdsWanted;
    private readonly bool forNewUser;
    private readonly Action<int?> finished;
    private readonly Action<string> log;
    private readonly List<double> taps = [];
    private readonly List<double> holds = [];
    private readonly TextBlock instruction = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15 };
    private readonly TextBlock progress = new() { FontWeight = FontWeight.Bold };
    private readonly TextBlock results = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Canvas timeline = new() { Width = TimelineWidth, Height = 92, ClipToBounds = false };
    private readonly Button useButton = new() { Classes = { "primary" }, IsVisible = false };
    private readonly Button keepButton = new() { IsVisible = false };
    private readonly Button againButton = new() { Content = "Measure again", IsVisible = false };
    private bool measuringHolds;
    private int suggestedMs;

    // finished receives the threshold to use, or null to keep the current one.
    // forNewUser words the prompts for someone who has never held a shortcut.
    public ZetlHoldMeasurePanel(
        double currentMs,
        int tapsWanted,
        int holdsWanted,
        bool forNewUser,
        Action<int?> finished,
        Action<string> log)
    {
        this.currentMs = currentMs;
        this.tapsWanted = tapsWanted;
        this.holdsWanted = holdsWanted;
        this.forNewUser = forNewUser;
        this.finished = finished;
        this.log = log;

        againButton.Click += (_, _) => Restart();
        keepButton.Click += (_, _) => finished(null);
        useButton.Click += (_, _) => finished(suggestedMs);

        Content = new StackPanel
        {
            Spacing = 14,
            Children =
            {
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
        };

        Restart();
    }

    public bool IsMeasuring => !results.IsVisible;

    public void RecordPress(double milliseconds)
    {
        if (results.IsVisible)
        {
            return;
        }

        if (!measuringHolds)
        {
            taps.Add(milliseconds);
            if (taps.Count == tapsWanted)
            {
                measuringHolds = true;
            }
        }
        else
        {
            holds.Add(milliseconds);
            if (holds.Count == holdsWanted)
            {
                ShowResults();
                return;
            }
        }

        againButton.IsVisible = true;
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
        againButton.IsVisible = false;
        UpdatePrompt();
        DrawTimeline();
    }

    private void UpdatePrompt()
    {
        if (!measuringHolds)
        {
            instruction.Text = forNewUser
                ? $"First, tap Ctrl+C {tapsWanted} times, the way you normally copy, letting go "
                  + "of both keys each time. Nothing will pop up while you do this."
                : $"First, your taps. Press Ctrl+C the way you always do when you copy, "
                  + $"{tapsWanted} times, letting go of Ctrl between them. Zetl is paused while "
                  + "this window is open, so nothing pops up.";
            progress.Text = $"Taps: {taps.Count} of {tapsWanted}";
        }
        else
        {
            instruction.Text = forNewUser
                ? $"Now press Ctrl+C and keep it down for a moment, as if pausing on purpose, "
                  + $"then let go. {holdsWanted} times."
                : $"Now your holds. Press Ctrl+C and keep holding it the way you would "
                  + $"to ask Zetl for something, then let go. {holdsWanted} times.";
            progress.Text = $"Holds: {holds.Count} of {holdsWanted}";
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
        var suggestion = !result.Separated
            ? $"Your taps and holds overlap, so no threshold separates them all. "
              + $"{suggestedMs} ms misjudges the fewest presses."
            : result.SuggestedHoldsTooShort == 0
                ? $"Suggested: {suggestedMs} ms, well clear of your taps, and every hold reaches it."
                : $"Suggested: {suggestedMs} ms, well clear of your taps. "
                  + $"{Plural(result.SuggestedHoldsTooShort, "hold")} fell just short of it; once "
                  + "Zetl is on, the ring shows you when a hold has landed.";

        results.Text = forNewUser
            ? $"Your taps were over by {result.SlowestTapMs:0} ms, and your holds lasted at least "
              + $"{result.QuickestHoldMs:0} ms. {suggestion}"
            : $"Taps: median {result.TapMedianMs:0} ms, slowest {result.SlowestTapMs:0} ms.\n"
              + $"Holds: quickest {result.QuickestHoldMs:0} ms, median {result.HoldMedianMs:0} ms.\n"
              + $"{atCurrent}\n{suggestion}";
        results.IsVisible = true;
        progress.Text = "Done.";
        instruction.Text = forNewUser
            ? "Here is where your taps end and your holds begin."
            : "Here is how your hands compare with the hold threshold.";
        useButton.Content = $"Use {suggestedMs} ms";
        useButton.IsVisible = suggestedMs != (int)currentMs;
        keepButton.Content = $"Keep {currentMs:0} ms";
        keepButton.IsVisible = true;
        againButton.IsVisible = true;
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
            // The last label ends at the axis end rather than running past it.
            AddText(tick == 1000 ? "1000+ ms" : $"{tick}", X(tick) - (tick == 1000 ? 48 : 8), 74, muted: true);
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
