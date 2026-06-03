using System.Text.Json;

namespace ZETL;

/// <summary>
/// Shared JSON persistence for Zetl's local state files: one serializer
/// configuration and an atomic temp-file-then-replace write so a crash
/// mid-write cannot leave a half-written file behind.
/// </summary>
internal static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static T? Read<T>(string path)
    {
        return File.Exists(path)
            ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
            : default;
    }

    public static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(value, Options));
        if (File.Exists(path))
        {
            File.Replace(tempPath, path, null);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }
}
