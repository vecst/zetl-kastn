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

    /// <summary>
    /// Reads JSON like <see cref="Read{T}"/>, but when the file is corrupt it
    /// moves the damaged file aside (a timestamped <c>.corrupt-*</c> copy) and
    /// returns <c>default</c> instead of throwing. Startup paths use this so a
    /// single bad file degrades to defaults — or is skipped — rather than
    /// aborting the whole load. The damaged content is preserved for recovery.
    /// </summary>
    public static T? ReadOrQuarantine<T>(string path, Action<string>? log = null)
    {
        try
        {
            return Read<T>(path);
        }
        catch (JsonException ex)
        {
            // Corrupt content: move it aside so it can't keep breaking startup.
            Quarantine(path, ex, log);
            return default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable/locked/denied: don't move a file we couldn't even read --
            // it may just be temporarily in use. Skip it for this load and keep
            // any valid sibling files loading.
            log?.Invoke($"Could not read '{path}' ({ex.GetType().Name}): {ex.Message}; using defaults for it.");
            return default;
        }
    }

    private static void Quarantine(string path, Exception reason, Action<string>? log)
    {
        var quarantinePath = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(path, quarantinePath, overwrite: true);
            log?.Invoke(
                $"Quarantined corrupt JSON '{path}' to '{Path.GetFileName(quarantinePath)}': {reason.Message}");
        }
        catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Could not quarantine corrupt JSON '{path}': {moveError.Message}");
        }
    }

    /// <summary>
    /// Deep copy through the same JSON path documents persist through, so a
    /// clone can be edited without disturbing the source (e.g. a built-in
    /// preset). Serializing a non-null object never yields JSON null, so the
    /// deserialized copy is always present.
    /// </summary>
    public static T Clone<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    public static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Unique per write so concurrent writers (two preview instances, or a
        // timer flush racing a UI-thread save) never contend on one temp file.
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
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
        finally
        {
            DeleteTempFile(tempPath);
        }
    }

    public static void WriteAtomicBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            DeleteTempFile(tempPath);
        }
    }

    public static void SweepStaleTempFiles(
        string rootDirectory,
        TimeSpan minimumAge,
        Action<string>? log = null)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - minimumAge;
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                rootDirectory,
                "*.tmp",
                SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) <= cutoff)
                    {
                        File.Delete(path);
                        log?.Invoke($"Removed stale temporary file '{path}'.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"Could not remove stale temporary file '{path}': {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Could not scan '{rootDirectory}' for stale temporary files: {ex.Message}");
        }
    }

    private static void DeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preserve the original write/replace exception. Startup sweeping will
            // retry abandoned unique temp files after they are old enough that no
            // concurrent writer can still own them.
        }
    }
}
