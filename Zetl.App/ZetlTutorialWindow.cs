using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace ZETL;

// What the host tells the tutorial as the user works through it.
internal enum ZetlTutorialSignal
{
    QuickNoteSaved,
    CaptureSaved,
    CutPastedBack,
    BoardOpened,
    CompilePasted
}

// What the tour needs from the host: the current settings it shows, and the
// changes it can make.
internal sealed record ZetlTutorialHost(
    int HoldDelayMs,
    string IndicatorStyle,
    Action<int> ApplyHoldDelay,
    Action<string> ApplyIndicatorStyle,
    Action<IZetlPressMeasurer?> SetMeasurer,
    Action Completed,
    Action<string> Log);

// The guided first run: the user measures their own taps and holds, then feels
// each core hold for real while this window watches for it to land: a quick
// note, a capture, a cut taken back, the Board, and Compile. It sits at the
// bottom centre of the screen, clear of the indicator in the top right and the
// popups near the top. Every step moves on when its action happens, and every
// step can be skipped. Finishing Compile, the last hold, completes the tour.
internal sealed class ZetlTutorialWindow : Window
{
    private const int TapsWanted = 10;
    private const int HoldsWanted = 10;
    private const double ScreenMargin = 24;

    // Francis Bacon, "Of Studies" (1625), in the public domain.
    private const string CopySentence =
        "Some books are to be tasted, others to be swallowed, and some few to be chewed and digested.";
    private const string CutSentence =
        "Reading maketh a full man; conference a ready man; and writing an exact man.";
    private const string Paragraph =
        "Studies serve for delight, for ornament, and for ability. … "
        + CopySentence + " … " + CutSentence;

    private enum Step
    {
        Welcome,
        Measure,
        QuickNote,
        Copy,
        Cut,
        Board,
        Compile,
        Indicator,
        Finish
    }

    private static readonly (Step Step, string Label)[] Chapters =
    [
        (Step.Measure, "Timing"),
        (Step.QuickNote, "Note"),
        (Step.Copy, "Copy"),
        (Step.Cut, "Cut"),
        (Step.Board, "Board"),
        (Step.Compile, "Compile"),
        (Step.Indicator, "Indicator")
    ];

    private readonly ZetlTutorialHost host;
    private readonly StackPanel chapterRow = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock stepTitle = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl body = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold, IsVisible = false };
    private readonly Button nextButton = new() { Classes = { "primary" }, MinWidth = 110 };
    private readonly Button skipStepButton = new() { Content = "Skip this step" };
    private readonly Button skipTour = new() { Content = "Skip the tour", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBox practice = new()
    {
        Text = Paragraph,
        TextWrapping = TextWrapping.Wrap,
        AcceptsReturn = false,
        FontSize = 16,
        Padding = new Thickness(12, 10),
        MinHeight = 96
    };
    private readonly TextBox pasteTarget = new()
    {
        Watermark = "Click here, then hold Ctrl+V",
        TextWrapping = TextWrapping.Wrap,
        AcceptsReturn = true,
        FontSize = 15,
        Padding = new Thickness(12, 10),
        MinHeight = 110,
        MaxHeight = 220
    };

    private Step step = Step.Welcome;
    private bool stepDone;
    private bool compilePasted;
    private string indicatorStyle;

    public ZetlTutorialWindow(ZetlTutorialHost host)
    {
        this.host = host;
        indicatorStyle = ZetlHoldIndicatorStyle.Normalize(host.IndicatorStyle);

        Title = "Getting to know Zetl";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        nextButton.Click += (_, _) => GoTo(step + 1);
        skipStepButton.Click += (_, _) =>
        {
            host.Log($"Tutorial: skipped {step}.");
            GoTo(step + 1);
        };
        skipTour.Click += (_, _) =>
        {
            host.Log($"Tutorial: left at {step}.");
            Close();
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(chapterRow);
        Grid.SetColumn(skipTour, 1);
        header.Children.Add(skipTour);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footer.Children.Add(skipStepButton);
        Grid.SetColumn(nextButton, 1);
        footer.Children.Add(nextButton);

        Content = new Border
        {
            Padding = new Thickness(22, 18),
            Child = new StackPanel
            {
                Spacing = 14,
                Children = { header, stepTitle, body, status, footer }
            }
        };

        practice.TextChanged += (_, _) => OnPracticeTextChanged();
        pasteTarget.TextChanged += (_, _) => CheckCompilePaste();
        Activated += (_, _) => SelectPracticeSentence();
        SizeChanged += (_, _) => PlaceAtBottomCentre();
        Opened += (_, _) => PlaceAtBottomCentre();
        Closed += (_, _) => host.SetMeasurer(null);

        GoTo(Step.Welcome);
    }

    // The host reports what just happened; a step that was waiting for it is done.
    public void OnSignal(ZetlTutorialSignal signal)
    {
        switch (step, signal)
        {
            case (Step.QuickNote, ZetlTutorialSignal.QuickNoteSaved):
                Done("Saved. It went to today's page in your Journal.");
                break;
            case (Step.Copy, ZetlTutorialSignal.CaptureSaved):
                Done("Captured. The copy went through as usual, and Zetl kept a note of it too.");
                break;
            case (Step.Cut, ZetlTutorialSignal.CutPastedBack):
                if (practice.Text?.Contains(CutSentence, StringComparison.Ordinal) == true)
                {
                    Done("Back where it was. Dismissing a quick note never loses what you cut.");
                }

                break;
            case (Step.Cut, ZetlTutorialSignal.QuickNoteSaved):
                // Saved instead of dismissed: the sentence is a note now. Put it
                // back so the Esc half can be tried.
                practice.Text = Paragraph;
                SelectPracticeSentence();
                Hint("You saved it as a note, which works too. Try once more, and press Esc this time.");
                break;
            case (Step.Board, ZetlTutorialSignal.BoardOpened):
                Done("That's the Board. Press Esc or click back here when you've had a look.");
                break;
            case (Step.Compile, ZetlTutorialSignal.CompilePasted):
                compilePasted = true;
                CheckCompilePaste();
                break;
        }
    }

    // For the preview: open on a given step.
    public void ShowStep(int index)
    {
        if (Enum.IsDefined(typeof(Step), index))
        {
            GoTo((Step)index);
        }
    }

    private void GoTo(Step next)
    {
        if (next > Step.Finish)
        {
            Close();
            return;
        }

        host.SetMeasurer(null);
        step = next;
        stepDone = false;
        status.IsVisible = false;
        host.Log($"Tutorial: {step}.");
        BuildChapterRow();

        skipStepButton.IsVisible = step is not (Step.Welcome or Step.Indicator or Step.Finish);
        skipTour.IsVisible = step != Step.Finish;
        nextButton.IsVisible = true;
        nextButton.IsEnabled = step is Step.Welcome or Step.Indicator or Step.Finish;
        nextButton.Content = step switch
        {
            Step.Welcome => "Start",
            Step.Finish => "Close",
            _ => "Next"
        };

        switch (step)
        {
            case Step.Welcome:
                stepTitle.Text = "Tap as always. Hold to ask Zetl.";
                body.Content = Text(
                    "Every shortcut you know still works the same when you tap it. Hold one a "
                    + "moment longer and Zetl steps in.\n\nThis short tour has you try it for real. "
                    + "It takes about three minutes, and you can skip any step.");
                break;

            case Step.Measure:
                stepTitle.Text = "First, how long is a tap for you?";
                nextButton.IsVisible = false;
                var panel = new ZetlHoldMeasurePanel(
                    host.HoldDelayMs,
                    TapsWanted,
                    HoldsWanted,
                    forNewUser: true,
                    milliseconds =>
                    {
                        if (milliseconds is { } chosen)
                        {
                            host.ApplyHoldDelay(chosen);
                        }

                        GoTo(Step.QuickNote);
                    },
                    host.Log);
                body.Content = panel;
                host.SetMeasurer(panel);
                break;

            case Step.QuickNote:
                stepTitle.Text = "Your first quick note";
                body.Content = Text(
                    "Keep an eye on the top right of your screen, then hold Ctrl+X until the ring "
                    + "fills. A quick note opens. Type a thought and press Ctrl+Enter to save it.\n\n"
                    + "Nothing is selected here, so there's nothing to cut and the note starts empty.");
                nextButton.Focus();
                break;

            case Step.Copy:
                stepTitle.Text = "Capture what you copy";
                body.Content = Practice(
                    "One sentence is selected for you. Tap Ctrl+C: that's an ordinary copy, and "
                    + "nothing pops up. Now hold Ctrl+C: the sentence opens in a capture. Press "
                    + "Ctrl+Enter to save it.");
                break;

            case Step.Cut:
                stepTitle.Text = "Cut, then change your mind";
                practice.Text = Paragraph;
                body.Content = Practice(
                    "Now hold Ctrl+X on the selected sentence. It leaves the text and lands in a "
                    + "quick note. Then press Esc: the note goes away and the sentence comes back.");
                break;

            case Step.Board:
                stepTitle.Text = "Where your notes went";
                body.Content = Text(
                    "Hold Ctrl+B to open the Board. It shows today's page in your Journal, with "
                    + "the note and the capture you just made.\n\n"
                    + "The Journal is where everything goes until you start a project of your "
                    + "own. The Board is where you rename, move, and tidy what you've collected.");
                nextButton.Focus();
                break;

            case Step.Compile:
                stepTitle.Text = "Bring your notes back out";
                compilePasted = false;
                pasteTarget.Text = "";
                body.Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        Text("Click in the box below, then hold Ctrl+V. Compile opens on your "
                            + "Journal. Tick what you want in it (Select All works), then press "
                            + "Paste Now, and your notes land in the box as one piece of text."),
                        pasteTarget
                    }
                };
                Dispatcher.UIThread.Post(() => pasteTarget.Focus());
                break;

            case Step.Indicator:
                stepTitle.Text = "How much should the indicator show?";
                body.Content = IndicatorChoices();
                break;

            case Step.Finish:
                stepTitle.Text = "You're set";
                body.Content = Text(
                    "Tap for the shortcut you know, hold for Zetl. That's all there is to it.\n\n"
                    + "Zetl keeps running in the tray, by the clock. Right-click its icon for "
                    + "Settings, to measure your taps and holds again, or to take this tour again.");
                break;
        }
    }

    private void Done(string message)
    {
        stepDone = true;
        status.Text = "✓ " + message;
        status.IsVisible = true;
        nextButton.IsEnabled = true;
        nextButton.Focus();
        host.Log($"Tutorial: {step} done.");
        if (step == Step.Compile)
        {
            host.Completed();
        }
    }

    private void Hint(string message)
    {
        status.Text = message;
        status.IsVisible = true;
    }

    private void OnPracticeTextChanged()
    {
        if (step == Step.Cut && !stepDone
            && practice.Text?.Contains(CutSentence, StringComparison.Ordinal) == false)
        {
            Hint("It's in the note now. Press Esc to bring it back.");
        }
    }

    // Compile's paste lands a moment after the host reports it, or the other
    // way round; the step is done once both have happened.
    private void CheckCompilePaste()
    {
        if (step == Step.Compile && !stepDone && compilePasted
            && !string.IsNullOrWhiteSpace(pasteTarget.Text))
        {
            Done("There they are. Compile gathers notes from any project into one piece of "
                + "text, ready to paste anywhere.");
        }
    }

    // The sentence the current step works on, selected in the practice text
    // whenever this window comes back to the front.
    private void SelectPracticeSentence()
    {
        var sentence = step switch
        {
            Step.Copy => CopySentence,
            Step.Cut => CutSentence,
            _ => null
        };
        if (sentence is null || stepDone || practice.Text is not { } text)
        {
            return;
        }

        var start = text.IndexOf(sentence, StringComparison.Ordinal);
        if (start < 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            practice.Focus();
            practice.SelectionStart = start;
            practice.SelectionEnd = start + sentence.Length;
        });
    }

    private Control Practice(string instructions)
    {
        var reselect = new Button { Content = "Select the sentence again" };
        reselect.Click += (_, _) => SelectPracticeSentence();
        // The one practice box moves from the copy step's layout to the cut's.
        (practice.Parent as Panel)?.Children.Remove(practice);
        SelectPracticeSentence();
        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Text(instructions),
                practice,
                new TextBlock { Text = "Francis Bacon, Of Studies (1625)", FontSize = 12, Classes = { "muted" } },
                reselect
            }
        };
    }

    // The three indicator styles as buttons; choosing one applies it at once,
    // so the next hold shows the difference.
    private Control IndicatorChoices()
    {
        var descriptions = new Dictionary<string, string>
        {
            [ZetlHoldIndicatorStyle.Detailed] =
                "The ring, plus the keys and how long you held them, on every press. Good while your hands learn the timing.",
            [ZetlHoldIndicatorStyle.Ring] =
                "Just the ring, and only once a press is clearly a hold. Quiet once the timing is second nature.",
            [ZetlHoldIndicatorStyle.Off] =
                "Nothing. The popup itself tells you the hold landed."
        };
        var buttons = new List<(string Id, Button Button)>();
        var list = new StackPanel { Spacing = 8 };
        foreach (var (id, label) in ZetlHoldIndicatorStyle.Choices)
        {
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 10),
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = id == ZetlHoldIndicatorStyle.Detailed ? "Detailed" : label, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = descriptions[id], TextWrapping = TextWrapping.Wrap, FontSize = 13 }
                    }
                }
            };
            var choice = id;
            button.Click += (_, _) =>
            {
                indicatorStyle = choice;
                host.ApplyIndicatorStyle(choice);
                Highlight();
                Hint("Try a hold now to see it. You can change this any time in Settings → Hold Actions.");
            };
            buttons.Add((id, button));
            list.Children.Add(button);
        }

        void Highlight()
        {
            foreach (var (id, button) in buttons)
            {
                button.Classes.Set("primary", id == indicatorStyle);
            }
        }

        Highlight();
        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Text("You've been watching the detailed overlay. Pick what you'd like from now on."),
                list
            }
        };
    }

    private void BuildChapterRow()
    {
        chapterRow.Children.Clear();
        if (step is Step.Welcome or Step.Finish)
        {
            return;
        }

        foreach (var (chapter, label) in Chapters)
        {
            var button = new Button
            {
                Content = label,
                Padding = new Thickness(10, 3),
                FontSize = 12
            };
            if (chapter == step)
            {
                button.Classes.Add("primary");
            }

            var target = chapter;
            button.Click += (_, _) => GoTo(target);
            chapterRow.Children.Add(button);
        }
    }

    private static TextBlock Text(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 15
    };

    // Bottom centre of the screen the window is on, kept there as the window
    // grows and shrinks between steps.
    private void PlaceAtBottomCentre()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null || Bounds.Height <= 0)
        {
            return;
        }

        // The frame, title bar included, is what has to fit above the taskbar.
        var size = FrameSize ?? Bounds.Size;
        var area = screen.WorkingArea;
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var width = size.Width * scaling;
        var height = size.Height * scaling;
        var margin = ScreenMargin * scaling;
        Position = new PixelPoint(
            (int)(area.X + (area.Width - width) / 2),
            (int)Math.Max(area.Y + margin, area.Bottom - height - margin));
    }
}
