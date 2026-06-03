using System.Text.Json;

namespace ZETL;

internal sealed class ZetlAppSettings
{
    public bool HasSeenFirstRun { get; set; }
}

internal sealed class ZetlAppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var tempPath = settingsPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(Settings, JsonOptions));
        if (File.Exists(settingsPath))
        {
            File.Replace(tempPath, settingsPath, null);
        }
        else
        {
            File.Move(tempPath, settingsPath);
        }
    }

    private ZetlAppSettings Load()
    {
        if (!File.Exists(settingsPath))
        {
            return new ZetlAppSettings();
        }

        var json = File.ReadAllText(settingsPath);
        return JsonSerializer.Deserialize<ZetlAppSettings>(json, JsonOptions) ?? new ZetlAppSettings();
    }
}
