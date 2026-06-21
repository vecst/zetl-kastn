using Avalonia.Controls;

namespace ZETL;

// Avalonia port of ZetlSettingsForm. It edits a snapshot and exposes the chosen
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
        ZetlAppSettingsStore? settingsStore = null)
    {
        InitializeComponent();
        ZetlWindowPlacement.Track(this);

        toastMsBox.Value = Clamp(settings.ToastDisplayMs, 200, 5000);
        autoCaptureBox.IsChecked = settings.AutoCaptureOnCopy;
        quickNoteClipboardBox.IsChecked = settings.QuickNoteToClipboard;
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

    private static decimal Clamp(int value, int min, int max)
    {
        return Math.Min(max, Math.Max(min, value));
    }
}
