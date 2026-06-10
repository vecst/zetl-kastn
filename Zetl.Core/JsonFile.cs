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
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            // Rethrown as JsonException so callers that fall back on invalid
            // files (user themes) still match, while surfaced errors name the
            // file that is damaged.
            throw new JsonException($"Failed to parse '{path}': {ex.Message}", ex);
        }
    }

    public static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Unique per write so concurrent writers (two preview instances, or a
        // timer flush racing a UI-thread save) never contend on one temp file.
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
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
