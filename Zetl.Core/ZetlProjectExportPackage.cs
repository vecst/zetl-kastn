using System.IO.Compression;
using System.Text.Json;

namespace ZETL;

internal sealed class ZetlProjectExportManifest
{
    public const int CurrentVersion = 1;

    public string Format { get; set; } = "zetl-project";

    public int Version { get; set; } = CurrentVersion;

    public string ProjectId { get; set; } = "";

    public string ProjectName { get; set; } = "";

    public DateTime ExportedAtUtc { get; set; }

    public bool CaptureOriginsIncluded { get; set; }

    public bool SourceUrlsIncluded { get; set; }

    public string ProjectEntry { get; set; } = "project.json";

    public int AssetCount { get; set; }
}

internal static class ZetlProjectExportPackage
{
    public const string ManifestEntryName = "manifest.json";
    public const string ProjectEntryName = "project.json";

    public static void Write(
        string path,
        ZetlProject project,
        bool includeCaptureOrigins,
        IReadOnlyList<ZetlProjectAssetFile>? assets = null,
        DateTime? exportedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Choose an export path.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("The export path has no parent directory.");
        Directory.CreateDirectory(directory);

        var snapshot = ZetlProjectExportSnapshot.Create(project, includeCaptureOrigins);
        var referencedAssets = snapshot.Buckets
            .SelectMany(bucket => bucket.Notes)
            .Where(note => note.IsImage && note.Image is not null)
            .Select(note => note.Image!.RelativePath.Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var assetFiles = (assets ?? [])
            .ToDictionary(asset => asset.RelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        var includedAssets = referencedAssets
            .Where(assetFiles.ContainsKey)
            .Select(path => assetFiles[path])
            .ToList();
        var manifest = new ZetlProjectExportManifest
        {
            ProjectId = snapshot.Id,
            ProjectName = snapshot.Name,
            ExportedAtUtc = exportedAtUtc ?? DateTime.UtcNow,
            CaptureOriginsIncluded = includeCaptureOrigins,
            SourceUrlsIncluded = includeCaptureOrigins,
            AssetCount = includedAssets.Count
        };
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteJson(archive, ManifestEntryName, manifest);
                WriteJson(archive, ProjectEntryName, snapshot);
                foreach (var asset in includedAssets)
                {
                    var entryName = asset.RelativePath.Replace('\\', '/');
                    if (!entryName.StartsWith("assets/", StringComparison.Ordinal)
                        || entryName.Contains("../", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    archive.CreateEntryFromFile(
                        asset.FullPath,
                        entryName,
                        CompressionLevel.Optimal);
                }
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void WriteJson<T>(ZipArchive archive, string entryName, T value)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, JsonFile.Options);
    }
}
