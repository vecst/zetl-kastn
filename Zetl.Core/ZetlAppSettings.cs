namespace ZETL;

internal sealed class ZetlAppSettings
{
    public bool HasSeenFirstRun { get; set; }
    public int ToastDisplayMs { get; set; } = 950;
    public bool AutoCaptureOnCopy { get; set; } = true;
    public bool QuickNoteToClipboard { get; set; }
    public List<string> DefaultProjectBuckets { get; set; } = new() { "Inbox", "Scratch" };
    public string DefaultCompileMode { get; set; } = "Formatted";
    public int DefaultTsvRowLength { get; set; } = 5;
    public string ThemeId { get; set; } = ZetlThemeDefaults.BuiltInId;
    public string ThemeVariant { get; set; } = "System";
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
