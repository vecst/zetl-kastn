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
    public bool KastnMinimizeAfterTemplate { get; set; } = true;
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
    private readonly string settingsPath;
    private readonly Action<string>? log;

    public ZetlAppSettingsStore(string? settingsPath = null, Action<string>? log = null)
    {
        this.settingsPath = settingsPath ?? Path.Combine(
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
