namespace ZETL;

internal sealed class ZetlAppSettings
{
    public bool HasSeenFirstRun { get; set; }
    public int ToastDisplayMs { get; set; } = 950;
    public bool AutoCaptureOnCopy { get; set; } = true;
    public bool QuickNoteToClipboard { get; set; }
    public bool ReplayResumeClipboard { get; set; } = true;
    public string CaptureOriginDetail { get; set; } = ZetlCaptureOriginDetail.ApplicationAndWindowTitle;
    public List<string> DefaultProjectBuckets { get; set; } = new() { "Inbox", "Scratch" };
    public string DefaultCompileMode { get; set; } = "Formatted";
    public int DefaultTsvRowLength { get; set; } = 5;
    // Hour (0-23, local) at which a new journal day begins, so late-night captures
    // land in the right dated bucket. 0 = midnight.
    public int DayStartHour { get; set; }
    // Hours of no capture after which an active deliberate project hands capture back
    // to the Journal, so a forgotten project never traps notes. 0 = off.
    public int JournalAutoReturnHours { get; set; }
    public string ThemeId { get; set; } = ZetlThemeDefaults.BuiltInId;
    public string ThemeVariant { get; set; } = "System";
    public string JournalInterval { get; set; } = ZetlJournalInterval.Weekly;

    // Kastn workbench preferences. Edited from Zetl's Settings window (the single
    // settings surface) and consumed by the Kastn process, which shares this file.
    public bool KastnAutosave { get; set; } = true;
    public string KastnStartup { get; set; } = ZetlKastnStartup.Landing;
    public string KastnDefaultViewId { get; set; } = "";
    public string KastnTemporaryTemplateLaneDefault { get; set; } = "";
    public bool KastnMinimizeAfterTemplate { get; set; } = true;
    public bool KastnMinimizeToTray { get; set; } = true;

    // Advanced Zetl and log settings.
    public int LogRetentionDays { get; set; } = 14;
    public int LogMaxNotesPerDay { get; set; } = 2000;
    public int LogFlushIntervalMs { get; set; } = 5000;
    public int MaxUndoActions { get; set; } = 100;
    public string UntitledSlipTitle { get; set; } = "Untitled";
    public int MaxSlipLabelLength { get; set; } = 24;
    public string PdfPageFormat { get; set; } = "Letter";
    public int PdfFontSize { get; set; } = 11;

    // Chordl Keyboard timings.
    public int HoldDelayMs { get; set; } = 353;
    public int RepeatSuppressionDelayMs { get; set; } = 33;

    // Advanced timings and timeouts.
    public int ClipboardPollIntervalMs { get; set; } = 20;
    public int ClipboardObservationTimeoutMs { get; set; } = 500;
    public int AutoCaptureClipboardTimeoutMs { get; set; } = 75;
    public int PopClipboardDelayMs { get; set; } = 75;
    public int ReplayClipboardRestoreDelayMs { get; set; } = 150;
    public int DownloadTimeoutSeconds { get; set; } = 10;
}

internal static class ZetlKastnStartup
{
    public const string Landing = "Landing";
    public const string LastProject = "LastProject";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), LastProject, StringComparison.OrdinalIgnoreCase)
            ? LastProject
            : Landing;
}

internal static class ZetlKastnTemplateLaneDefault
{
    public const string Ask = "";

    public static string Normalize(string? value)
    {
        var lane = ZetlStateStore.CanonicalTemporaryLane(value);
        return lane ?? Ask;
    }
}

internal static class ZetlJournalInterval
{
    public const string Daily = "Daily";
    public const string Weekly = "Weekly";
    public const string Monthly = "Monthly";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Weekly, StringComparison.OrdinalIgnoreCase) ? Weekly
        : string.Equals(value?.Trim(), Monthly, StringComparison.OrdinalIgnoreCase) ? Monthly
        : Daily;
}

internal sealed class ZetlAppSettingsStore
{
    public static string? DefaultSettingsPathOverride { get; set; }

    private readonly string settingsPath;
    private readonly Action<string>? log;

    public ZetlAppSettingsStore(string? settingsPath = null, Action<string>? log = null)
    {
        this.settingsPath = settingsPath ?? DefaultSettingsPathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Zetl",
            "settings.json");
        this.log = log;
        Settings = Load();
    }

    public ZetlAppSettings Settings { get; private set; }

    public string SettingsPath => settingsPath;

    public void MarkFirstRunSeen()
    {
        Settings.HasSeenFirstRun = true;
        Save();
    }

    public void Save()
    {
        JsonFile.WriteAtomic(settingsPath, Settings);
    }

    private ZetlAppSettings Load()
    {
        return JsonFile.ReadOrQuarantine<ZetlAppSettings>(settingsPath, log) ?? new ZetlAppSettings();
    }
}
