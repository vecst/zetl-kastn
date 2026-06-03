namespace ZETL;

internal sealed class ZetlAppSettings
{
    public bool HasSeenFirstRun { get; set; }
}

internal sealed class ZetlAppSettingsStore
{
    private readonly string settingsPath;

    public ZetlAppSettingsStore(string? settingsPath = null)
    {
        this.settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Zetl",
            "settings.json");
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
        return JsonFile.Read<ZetlAppSettings>(settingsPath) ?? new ZetlAppSettings();
    }
}
