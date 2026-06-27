using Avalonia.Controls;

namespace ZETL;

// Settings window. It edits a snapshot and exposes the chosen
// values; persistence remains the controller's responsibility.
internal partial class ZetlSettingsWindow : Window
{
    public ZetlSettingsWindow()
        : this(new ZetlAppSettings())
    {
    }

    internal ZetlSettingsWindow(
        ZetlAppSettings settings,
        ZetlThemeManager? themeManager = null,
        ZetlThemeStore? themeStore = null,
        ZetlAppSettingsStore? settingsStore = null,
        ZetlViewStore? viewStore = null)
    {
        InitializeComponent();
        ZetlWindowPlacement.Track(this);

        toastMsBox.Value = Clamp(settings.ToastDisplayMs, 200, 5000);
        autoCaptureBox.IsChecked = settings.AutoCaptureOnCopy;
        quickNoteClipboardBox.IsChecked = settings.QuickNoteToClipboard;
        replayResumeClipboardBox.IsChecked = settings.ReplayResumeClipboard;
        captureOriginBox.ItemsSource = new[]
        {
            "Off",
            "Application only",
            "Application and window title"
        };
        captureOriginBox.SelectedIndex = ZetlCaptureOriginDetail.Normalize(
            settings.CaptureOriginDetail) switch
        {
            ZetlCaptureOriginDetail.Off => 0,
            ZetlCaptureOriginDetail.ApplicationOnly => 1,
            _ => 2
        };
        defaultBucketsBox.Text = string.Join(Environment.NewLine, settings.DefaultProjectBuckets);
        compileModeBox.ItemsSource = new[] { "Formatted", "Plain", "TSV" };
        compileModeBox.SelectedItem = settings.DefaultCompileMode is "Plain" or "TSV"
            ? settings.DefaultCompileMode
            : "Formatted";
        tsvRowLengthBox.Value = Clamp(settings.DefaultTsvRowLength, 1, 50);
        dayStartHourBox.Value = Clamp(settings.DayStartHour, 0, 23);
        autoReturnHoursBox.Value = Clamp(settings.JournalAutoReturnHours, 0, 168);
        journalIntervalBox.ItemsSource = new[] { "Daily", "Weekly", "Monthly" };
        journalIntervalBox.SelectedItem = ZetlJournalInterval.Normalize(settings.JournalInterval);

        // Kastn workbench preferences (consumed by the Kastn process via the shared
        // settings file; Zetl just hosts the one settings surface).
        kastnAutosaveBox.IsChecked = settings.KastnAutosave;
        kastnStartupBox.ItemsSource = new[] { "Landing page", "Last opened project" };
        kastnStartupBox.SelectedIndex =
            ZetlKastnStartup.Normalize(settings.KastnStartup) == ZetlKastnStartup.LastProject ? 1 : 0;

        var viewChoices = new List<ViewChoice> { new("", "Project default") };
        viewChoices.AddRange((viewStore ?? new ZetlViewStore()).LoadAll()
            .OrderBy(view => view.Name, StringComparer.OrdinalIgnoreCase)
            .Select(view => new ViewChoice(view.Id, view.Name)));
        kastnDefaultViewBox.ItemsSource = viewChoices;
        kastnDefaultViewBox.SelectedItem =
            viewChoices.FirstOrDefault(choice => choice.Id == settings.KastnDefaultViewId)
            ?? viewChoices[0];

        kastnMinimizeAfterTemplateBox.IsChecked = settings.KastnMinimizeAfterTemplate;
        kastnMinimizeToTrayBox.IsChecked = settings.KastnMinimizeToTray;

        saveButton.Click += (_, _) =>
        {
            Saved = true;
            Close();
        };
        cancelButton.Click += (_, _) => Close();
        themeButton.IsVisible = themeManager is not null
            && themeStore is not null
            && settingsStore is not null;
        themeButton.Click += async (_, _) =>
        {
            if (themeManager is not null
                && themeStore is not null
                && settingsStore is not null)
            {
                await new ThemeEditorWindow(
                    themeManager,
                    themeStore,
                    settingsStore).ShowDialog(this);
            }
        };
    }

    public bool Saved { get; private set; }

    public int ToastDisplayMs => (int)(toastMsBox.Value ?? 950);

    public bool AutoCaptureOnCopy => autoCaptureBox.IsChecked == true;

    public bool QuickNoteToClipboard => quickNoteClipboardBox.IsChecked == true;

    public bool ReplayResumeClipboard => replayResumeClipboardBox.IsChecked == true;

    public string CaptureOriginDetail => captureOriginBox.SelectedIndex switch
    {
        0 => ZetlCaptureOriginDetail.Off,
        1 => ZetlCaptureOriginDetail.ApplicationOnly,
        _ => ZetlCaptureOriginDetail.ApplicationAndWindowTitle
    };

    public List<string> DefaultProjectBuckets => (defaultBucketsBox.Text ?? "")
        .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .ToList();

    public string DefaultCompileMode => compileModeBox.SelectedItem as string ?? "Formatted";

    public int DefaultTsvRowLength => (int)(tsvRowLengthBox.Value ?? 5);

    public int DayStartHour => (int)(dayStartHourBox.Value ?? 0);

    public int JournalAutoReturnHours => (int)(autoReturnHoursBox.Value ?? 0);

    public string JournalInterval => journalIntervalBox.SelectedItem as string ?? "Weekly";

    public bool KastnAutosave => kastnAutosaveBox.IsChecked == true;

    public string KastnStartup => kastnStartupBox.SelectedIndex == 1
        ? ZetlKastnStartup.LastProject
        : ZetlKastnStartup.Landing;

    public string KastnDefaultViewId => (kastnDefaultViewBox.SelectedItem as ViewChoice)?.Id ?? "";

    public bool KastnMinimizeAfterTemplate => kastnMinimizeAfterTemplateBox.IsChecked == true;

    public bool KastnMinimizeToTray => kastnMinimizeToTrayBox.IsChecked == true;

    private static decimal Clamp(int value, int min, int max)
    {
        return Math.Min(max, Math.Max(min, value));
    }

    private sealed record ViewChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }
}
