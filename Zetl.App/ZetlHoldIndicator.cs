using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace ZETL;

// The hold indicator: a ring that fills while a shortcut is held and completes
// when its hold action fires. On its own the ring appears only once a press has
// clearly outlasted a tap, so ordinary copies and pastes stay invisible. The
// detailed overlay adds the key, a millisecond timer, and the action's name,
// and shows taps too, so the line between a tap and a hold can be seen.
// Every method runs on the UI thread; the keyboard hook only posts.
internal sealed class ZetlHoldIndicator
{
    // The ring alone appears this far into the hold delay: past even a slow
    // tap, which the hold delay is set well clear of, so it never flashes on
    // an ordinary copy, yet early enough to watch the hold fill.
    private const double ShowAfterFraction = 0.5;
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan DetailedLinger = TimeSpan.FromMilliseconds(900);
    private const double RingSize = 44;
    private const double NormalWidth = 56;
    private const double DetailedWidth = 300;
    private const double WindowHeight = 56;

    private readonly DispatcherTimer frameTimer;
    private Window? window;
    private Arc progress = null!;
    private Ellipse track = null!;
    private StackPanel detailPanel = null!;
    private TextBlock keyText = null!;
    private TextBlock timerText = null!;
    private TextBlock actionText = null!;

    private Phase phase = Phase.Idle;
    private long startedAt;
    private TimeSpan holdDelay;
    private TimeSpan frozenElapsed;
    private long settledAt;
    private string? actionLabel;

    public ZetlHoldIndicator()
    {
        frameTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(15),
            DispatcherPriority.Render,
            (_, _) => OnFrame());
    }

    public bool Enabled { get; set; } = true;

    public bool Detailed { get; set; }

    // The anchor id the indicator is placed at (already resolved from settings).
    public string Anchor { get; set; } = ZetlScreenAnchor.TopCenter;

    private enum Phase
    {
        Idle,
        Holding,
        Completed,
        Released,
        Fading
    }

    // A configured chord went down. actionLabel is the hold's action, or null
    // when holding it would do nothing; then only the detailed overlay shows.
    public void Start(string comboName, long timestamp, TimeSpan delay, string? holdActionLabel)
    {
        ZetlTrace.Write($"indicator: start {comboName}");
        if (!Enabled || (holdActionLabel is null && !Detailed))
        {
            Hide();
            return;
        }

        EnsureWindow();
        startedAt = timestamp;
        holdDelay = delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(353) : delay;
        actionLabel = holdActionLabel;
        phase = Phase.Holding;
        keyText.Text = comboName.Replace("+", " + ");
        actionText.Text = "";
        timerText.Text = "0 ms";
        progress.SweepAngle = 0;
        track.Opacity = 1;
        window!.Opacity = 1;
        frameTimer.Start();
        OnFrame();
    }

    // The hold fired and its action runs: complete the ring.
    public void Complete()
    {
        if (phase != Phase.Holding)
        {
            return;
        }

        frozenElapsed = Stopwatch.GetElapsedTime(startedAt);
        phase = Phase.Completed;
        settledAt = Stopwatch.GetTimestamp();
        progress.SweepAngle = 360;
        timerText.Text = $"{frozenElapsed.TotalMilliseconds:0} ms";
        actionText.Text = actionLabel is null ? "" : $"→ {actionLabel}";
        ShowAt();
    }

    // The keys were released (or Ctrl let go): a tap if no hold fired.
    public void End(bool held)
    {
        if (phase == Phase.Holding)
        {
            frozenElapsed = Stopwatch.GetElapsedTime(startedAt);
            if (!Detailed)
            {
                // A released press never completes the ring; it just goes.
                Hide();
                return;
            }

            phase = Phase.Released;
            settledAt = Stopwatch.GetTimestamp();
            timerText.Text = $"{frozenElapsed.TotalMilliseconds:0} ms";
            actionText.Text = held ? "" : "tap";
            ShowAt();
        }
        else if (phase == Phase.Completed && !Detailed)
        {
            phase = Phase.Fading;
            settledAt = Stopwatch.GetTimestamp();
        }
    }

    private void OnFrame()
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        switch (phase)
        {
            case Phase.Holding:
                if (!window!.IsVisible && (Detailed || elapsed >= holdDelay * ShowAfterFraction))
                {
                    ShowAt();
                }

                // Stop just short of full: only the real hold completes the ring,
                // so a busy machine never shows a hold that has not fired.
                progress.SweepAngle = Math.Min(0.97, elapsed / holdDelay) * 360;
                timerText.Text = $"{elapsed.TotalMilliseconds:0} ms";
                break;

            case Phase.Completed:
            case Phase.Released:
                // The ring alone lingers briefly; the detailed overlay keeps
                // the result up long enough to read.
                var linger = Detailed ? DetailedLinger : FadeOut;
                if (Stopwatch.GetElapsedTime(settledAt) >= linger)
                {
                    phase = Phase.Fading;
                    settledAt = Stopwatch.GetTimestamp();
                }

                break;

            case Phase.Fading:
                var fade = Stopwatch.GetElapsedTime(settledAt) / FadeOut;
                if (fade >= 1)
                {
                    Hide();
                }
                else
                {
                    ZetlTrace.Write($"indicator: fade {fade:0.00}");
                    window!.Opacity = 1 - fade;
                }

                break;

            default:
                frameTimer.Stop();
                break;
        }
    }

    private void ShowAt()
    {
        if (window is null)
        {
            return;
        }

        var width = Detailed ? DetailedWidth : NormalWidth;
        ZetlTrace.Write("indicator: show begin");
        window.Width = width;
        detailPanel.IsVisible = Detailed;
        ZetlWindowPlacement.PlaceOverlay(window, width, WindowHeight, Anchor);
        ZetlTrace.Write("indicator: placed");
        if (!window.IsVisible)
        {
            window.Show();
            ZetlTrace.Write("indicator: shown");
            MakeNonActivating(window);
            ZetlTrace.Write("indicator: styled");
        }

        ReassertTopmost(window);
        ZetlTrace.Write("indicator: show end");
    }

    private void Hide()
    {
        ZetlTrace.Write("indicator: hide begin");
        phase = Phase.Idle;
        frameTimer.Stop();
        window?.Hide();
        ZetlTrace.Write("indicator: hide end");
    }

    private void EnsureWindow()
    {
        if (window is not null)
        {
            return;
        }

        track = new Ellipse
        {
            Width = RingSize,
            Height = RingSize,
            StrokeThickness = 5
        };
        track.Bind(Shape.StrokeProperty, track.GetResourceObservable("ZetlBorderBrush"));
        progress = new Arc
        {
            Width = RingSize,
            Height = RingSize,
            StrokeThickness = 5,
            StartAngle = -90,
            SweepAngle = 0,
            StrokeLineCap = PenLineCap.Round,
            // Avalonia's arc sweeps clockwise from 12 o'clock; mirroring it makes
            // the ring fill counter-clockwise. That's deliberate: it's Zetl's
            // look, and the logo and website ring fill the same way.
            RenderTransform = new ScaleTransform(-1, 1)
        };
        progress.Bind(Shape.StrokeProperty, progress.GetResourceObservable("ZetlAccentBrush"));

        var ring = new Grid
        {
            Width = RingSize,
            Height = RingSize,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { track, progress }
        };

        keyText = new TextBlock { FontWeight = FontWeight.Bold, FontSize = 16 };
        timerText = new TextBlock { FontSize = 14, MinWidth = 64 };
        actionText = new TextBlock { FontSize = 14 };
        detailPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Children =
            {
                keyText,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { timerText, actionText }
                }
            }
        };

        var content = new Border
        {
            CornerRadius = new CornerRadius(WindowHeight / 2),
            Padding = new Thickness(6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { ring, detailPanel }
            }
        };
        content.Bind(Border.BackgroundProperty, content.GetResourceObservable("ZetlSurfaceBrush"));

        window = new Window
        {
            Width = NormalWidth,
            Height = WindowHeight,
            CanResize = false,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            SystemDecorations = SystemDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            WindowStartupLocation = WindowStartupLocation.Manual,
            Focusable = false,
            Title = "Zetl hold indicator",
            Content = content
        };
    }

    // A tool window that never takes focus, even when clicked, so the indicator
    // can't pull the user out of what they were typing into.
    private static void MakeNonActivating(Window target)
    {
        if (!OperatingSystem.IsWindows() || target.TryGetPlatformHandle()?.Handle is not { } handle)
        {
            return;
        }

        var style = Win32Interop.GetWindowLongPtr(handle, Win32Interop.GWL_EXSTYLE).ToInt64();
        Win32Interop.SetWindowLongPtr(
            handle,
            Win32Interop.GWL_EXSTYLE,
            new IntPtr(style | Win32Interop.WS_EX_NOACTIVATE | Win32Interop.WS_EX_TOOLWINDOW));
    }

    // Climb back above a full-screen video or another topmost window without
    // taking focus, as the toast does.
    private static void ReassertTopmost(Window target)
    {
        if (!OperatingSystem.IsWindows() || target.TryGetPlatformHandle()?.Handle is not { } handle)
        {
            return;
        }

        Win32Interop.SetWindowPos(
            handle,
            Win32Interop.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);
    }
}
