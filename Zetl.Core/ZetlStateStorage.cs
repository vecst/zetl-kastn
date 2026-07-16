namespace ZETL;

/// <summary>
/// On-disk layout for Zetl state. The workspace root holds a small
/// <c>workspace.json</c> with the version and the two active-project pointers,
/// and every project lives in its own <c>projects/&lt;name&gt;-&lt;id&gt;/project.json</c>.
/// Splitting projects into separate files keeps each save small: capturing a
/// note rewrites only the affected project, never the whole store.
///
/// The folder is named from a sanitized project name plus a short slice of the
/// project id. The id slice keeps folders unique and stable, so renaming a
/// project only rewrites a field inside its json -- the folder never has to
/// move.
/// </summary>
internal sealed class ZetlStateStorage : IZetlStateStorage
{
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromDays(1);

    private readonly string rootDirectory;
    private readonly string workspacePath;
    private readonly string projectsDirectory;
    private readonly string? legacyStatePath;
    private readonly Action<string>? log;

    // project id -> folder name under projectsDirectory. Rebuilt on load and
    // extended as new projects are written.
    private readonly Dictionary<string, string> projectFolders = new(StringComparer.Ordinal);

    public ZetlStateStorage(string rootDirectory, string? legacyStatePath, Action<string>? log = null)
    {
        this.rootDirectory = rootDirectory;
        workspacePath = Path.Combine(rootDirectory, "workspace.json");
        projectsDirectory = Path.Combine(rootDirectory, "projects");
        this.legacyStatePath = legacyStatePath;
        this.log = log;
    }

    public ZetlState Load()
    {
        JsonFile.SweepStaleTempFiles(rootDirectory, StaleTempAge, log);
        MigrateLegacyStateIfNeeded();

        var workspace = JsonFile.ReadOrQuarantine<ZetlWorkspaceFile>(workspacePath, log) ?? new ZetlWorkspaceFile();
        var state = new ZetlState
        {
            Version = workspace.Version,
            ActiveProjectId = workspace.ActiveProjectId,
            ShiftActiveProjectId = workspace.ShiftActiveProjectId,
            DefaultJournalProjectId = workspace.DefaultJournalProjectId,
            ShiftDefaultJournalProjectId = workspace.ShiftDefaultJournalProjectId,
            LastDeliberateProjectId = workspace.LastDeliberateProjectId,
            ShiftLastDeliberateProjectId = workspace.ShiftLastDeliberateProjectId,
            Projects = ReadProjects()
        };
        return state;
    }

    public void WriteProject(ZetlProject project)
    {
        var folder = GetOrAssignFolder(project);
        JsonFile.WriteAtomic(Path.Combine(projectsDirectory, folder, "project.json"), project);
    }

    public string WriteAsset(
        ZetlProject project,
        string contentHash,
        string extension,
        byte[] bytes)
    {
        var fileName = $"{contentHash}{extension}";
        var relativePath = $"assets/{fileName}";
        var fullPath = ResolveAssetPath(project, relativePath, createProjectDirectory: true)
            ?? throw new InvalidDataException("Could not resolve the project asset path.");
        if (!File.Exists(fullPath))
        {
            JsonFile.WriteAtomicBytes(fullPath, bytes);
        }

        return relativePath;
    }

    public byte[]? ReadAsset(ZetlProject project, string relativePath)
    {
        var path = ResolveAssetPath(project, relativePath);
        try
        {
            return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? GetAssetPath(ZetlProject project, string relativePath)
    {
        var path = ResolveAssetPath(project, relativePath);
        return path is not null && File.Exists(path) ? path : null;
    }

    public IReadOnlyList<ZetlProjectAssetFile> GetAssets(ZetlProject project)
    {
        var directory = ResolveAssetPath(project, "assets", createProjectDirectory: false);
        if (directory is null || !Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.GetFiles(directory)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new ZetlProjectAssetFile(
                    $"assets/{Path.GetFileName(path)}",
                    path))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private string? ResolveAssetPath(
        ZetlProject project,
        string relativePath,
        bool createProjectDirectory = false)
    {
        var folder = GetOrAssignFolder(project);
        var projectDirectory = Path.GetFullPath(Path.Combine(projectsDirectory, folder));
        if (createProjectDirectory)
        {
            Directory.CreateDirectory(projectDirectory);
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(projectDirectory, normalized));
        var prefix = projectDirectory.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : null;
    }

    public void WriteWorkspace(ZetlWorkspaceFile workspace)
    {
        JsonFile.WriteAtomic(workspacePath, workspace);
    }

    public void RemoveProject(string projectId)
    {
        if (!projectFolders.TryGetValue(projectId, out var folder))
        {
            return;
        }

        projectFolders.Remove(projectId);
        var directory = Path.Combine(projectsDirectory, folder);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private List<ZetlProject> ReadProjects()
    {
        projectFolders.Clear();
        var projects = new List<ZetlProject>();
        if (!Directory.Exists(projectsDirectory))
        {
            return projects;
        }

        foreach (var directory in Directory.EnumerateDirectories(projectsDirectory))
        {
            // ReadOrQuarantine moves a corrupt project.json aside and returns
            // null, so one damaged project is skipped while every valid project
            // still loads -- a single bad file no longer aborts the whole store.
            var project = JsonFile.ReadOrQuarantine<ZetlProject>(Path.Combine(directory, "project.json"), log);
            if (project is null || string.IsNullOrWhiteSpace(project.Id))
            {
                continue;
            }

            projectFolders[project.Id] = Path.GetFileName(directory);
            projects.Add(project);
        }

        return projects;
    }

    private string GetOrAssignFolder(ZetlProject project)
    {
        if (projectFolders.TryGetValue(project.Id, out var existing))
        {
            return existing;
        }

        var folder = UniqueFolderName(project);
        projectFolders[project.Id] = folder;
        return folder;
    }

    private string UniqueFolderName(ZetlProject project)
    {
        var shortId = ShortId(project.Id);
        var baseName = $"{SanitizeFolderName(project.Name)}-{shortId}";
        var candidate = baseName;
        var suffix = 2;
        while (IsFolderTaken(candidate))
        {
            candidate = $"{baseName}-{suffix++}";
        }

        return candidate;
    }

    private bool IsFolderTaken(string folder)
    {
        return projectFolders.Values.Contains(folder, StringComparer.OrdinalIgnoreCase)
            || Directory.Exists(Path.Combine(projectsDirectory, folder));
    }

    private void MigrateLegacyStateIfNeeded()
    {
        if (File.Exists(workspacePath)
            || (Directory.Exists(projectsDirectory) && Directory.EnumerateDirectories(projectsDirectory).Any())
            || legacyStatePath is null
            || !File.Exists(legacyStatePath))
        {
            return;
        }

        var legacy = JsonFile.ReadOrQuarantine<ZetlState>(legacyStatePath, log);
        if (legacy is null)
        {
            return;
        }

        projectFolders.Clear();
        foreach (var project in legacy.Projects)
        {
            if (!string.IsNullOrWhiteSpace(project.Id))
            {
                WriteProject(project);
            }
        }

        WriteWorkspace(new ZetlWorkspaceFile
        {
            Version = Math.Max(legacy.Version, 1),
            ActiveProjectId = legacy.ActiveProjectId,
            ShiftActiveProjectId = legacy.ShiftActiveProjectId,
            DefaultJournalProjectId = legacy.DefaultJournalProjectId,
            ShiftDefaultJournalProjectId = legacy.ShiftDefaultJournalProjectId,
            LastDeliberateProjectId = legacy.LastDeliberateProjectId,
            ShiftLastDeliberateProjectId = legacy.ShiftLastDeliberateProjectId
        });

        // Keep the original file as a backup rather than deleting it outright.
        // The migrated project/workspace files are already written, so don't let
        // a locked or access-denied rename abort startup -- just log and move on.
        var backupPath = legacyStatePath + ".bak";
        try
        {
            File.Move(legacyStatePath, backupPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Migrated legacy state, but renaming '{legacyStatePath}' to its backup failed: {ex.Message}");
        }
    }

    private static string ShortId(string id)
    {
        var trimmed = id.Trim();
        return trimmed.Length <= 6 ? trimmed : trimmed[..6];
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            builder.Append(invalid.Contains(ch) || ch == ' ' ? '-' : ch);
        }

        // Collapse runs of dashes and trim them from the ends so names stay tidy.
        var collapsed = builder.ToString();
        while (collapsed.Contains("--", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("--", "-");
        }

        collapsed = collapsed.Trim('-');
        if (collapsed.Length > 60)
        {
            collapsed = collapsed[..60].Trim('-');
        }

        return collapsed.Length == 0 ? "project" : collapsed;
    }
}

internal sealed record ZetlProjectAssetFile(string RelativePath, string FullPath);

/// <summary>
/// Serialized shape of <c>workspace.json</c>: the store-wide version and the
/// two lanes' routing and recovery pointers. Projects themselves live in their own
/// files, so they are intentionally absent here.
/// </summary>
internal sealed class ZetlWorkspaceFile
{
    public int Version { get; set; } = 1;
    public string? ActiveProjectId { get; set; }
    public string? ShiftActiveProjectId { get; set; }
    public string? DefaultJournalProjectId { get; set; }
    public string? ShiftDefaultJournalProjectId { get; set; }
    public string? LastDeliberateProjectId { get; set; }
    public string? ShiftLastDeliberateProjectId { get; set; }
}
