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
    internal const string PendingRemovalMarkerFileName = "removal.json";
    internal const string PreparedRemovalStatus = "Prepared";
    internal const string CommittedRemovalStatus = "Committed";

    private readonly string rootDirectory;
    private readonly string workspacePath;
    private readonly string projectsDirectory;
    private readonly string pendingRemovalsDirectory;
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
        pendingRemovalsDirectory = Path.Combine(rootDirectory, "pending-project-removals");
        this.legacyStatePath = legacyStatePath;
        this.log = log;
    }

    public ZetlState Load()
    {
        JsonFile.SweepStaleTempFiles(rootDirectory, StaleTempAge, log);
        RecoverPendingProjectRemovals();
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

    public IZetlProjectRemoval PrepareProjectRemoval(string projectId)
    {
        if (!projectFolders.TryGetValue(projectId, out var folder))
        {
            return NoProjectRemoval.Instance;
        }

        var projectDirectory = Path.Combine(projectsDirectory, folder);
        string? pendingDirectory = null;
        if (Directory.Exists(projectDirectory))
        {
            Directory.CreateDirectory(pendingRemovalsDirectory);
            pendingDirectory = Path.Combine(
                pendingRemovalsDirectory,
                $"{folder}.{Guid.NewGuid():N}");
            try
            {
                Directory.Move(projectDirectory, pendingDirectory);
                JsonFile.WriteAtomic(
                    Path.Combine(pendingDirectory, PendingRemovalMarkerFileName),
                    new ZetlPendingProjectRemovalFile
                    {
                        ProjectId = projectId,
                        OriginalFolder = folder,
                        Status = PreparedRemovalStatus
                    });
            }
            catch
            {
                // Preparation is not complete until its recovery marker is
                // durable. Put the whole directory back before reporting failure.
                try
                {
                    if (Directory.Exists(pendingDirectory)
                        && !Directory.Exists(projectDirectory))
                    {
                        Directory.Move(pendingDirectory, projectDirectory);
                        DeleteRemovalMarker(projectDirectory);
                    }
                }
                catch (Exception rollbackError) when (
                    rollbackError is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke(
                        $"Could not restore project '{projectId}' after removal preparation failed: {rollbackError.Message}");
                }

                throw;
            }
        }

        projectFolders.Remove(projectId);
        return new ProjectRemoval(
            this,
            projectId,
            folder,
            projectDirectory,
            pendingDirectory);
    }

    private sealed class ProjectRemoval(
        ZetlStateStorage owner,
        string projectId,
        string folder,
        string projectDirectory,
        string? pendingDirectory) : IZetlProjectRemoval
    {
        private RemovalState state;

        public void Commit()
        {
            if (state != RemovalState.Prepared)
            {
                return;
            }

            state = RemovalState.Committed;
            if (pendingDirectory is null || !Directory.Exists(pendingDirectory))
            {
                return;
            }

            try
            {
                // Mark the directory before cleanup. A crash or locked-directory
                // failure after this point is completed by the next startup.
                JsonFile.WriteAtomic(
                    Path.Combine(pendingDirectory, PendingRemovalMarkerFileName),
                    new ZetlPendingProjectRemovalFile
                    {
                        ProjectId = projectId,
                        OriginalFolder = folder,
                        Status = CommittedRemovalStatus
                    });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Continue with cleanup. If cleanup also fails, startup restores
                // the unmarked/prepared directory rather than risking data loss.
                owner.log?.Invoke(
                    $"Could not mark removal of project '{projectId}' as committed: {ex.Message}");
            }

            try
            {
                Directory.Delete(pendingDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The live project is already durably removed. Keep the pending
                // directory for a later cleanup pass rather than turning trash
                // cleanup into a failed domain transaction.
                owner.log?.Invoke(
                    $"Could not finalize removal of project '{projectId}': {ex.Message}");
            }
        }

        public void RollBack()
        {
            if (state != RemovalState.Prepared)
            {
                return;
            }

            if (pendingDirectory is not null && Directory.Exists(pendingDirectory))
            {
                if (Directory.Exists(projectDirectory))
                {
                    throw new IOException(
                        $"Could not restore project '{projectId}': its directory already exists.");
                }

                Directory.Move(pendingDirectory, projectDirectory);
                DeleteRemovalMarker(projectDirectory);
            }

            owner.projectFolders[projectId] = folder;
            state = RemovalState.RolledBack;
        }

        public void Dispose()
        {
            if (state == RemovalState.Prepared)
            {
                try
                {
                    RollBack();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Disposal must not hide the transaction failure that led
                    // here. The explicit rollback caller already reports its
                    // own failure; this is the final best-effort safety net.
                    owner.log?.Invoke(
                        $"Could not dispose prepared removal for project '{projectId}': {ex.Message}");
                }
            }
        }
    }

    private sealed class NoProjectRemoval : IZetlProjectRemoval
    {
        public static NoProjectRemoval Instance { get; } = new();

        public void Commit()
        {
        }

        public void RollBack()
        {
        }

        public void Dispose()
        {
        }
    }

    private enum RemovalState
    {
        Prepared,
        Committed,
        RolledBack
    }

    private void RecoverPendingProjectRemovals()
    {
        if (!Directory.Exists(pendingRemovalsDirectory))
        {
            return;
        }

        try
        {
            foreach (var pendingDirectory in Directory
                .EnumerateDirectories(pendingRemovalsDirectory)
                .ToList())
            {
                RecoverPendingProjectRemoval(pendingDirectory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke(
                $"Could not scan pending project removals in '{pendingRemovalsDirectory}': {ex.Message}");
        }
    }

    private void RecoverPendingProjectRemoval(string pendingDirectory)
    {
        var markerPath = Path.Combine(pendingDirectory, PendingRemovalMarkerFileName);
        var marker = JsonFile.ReadOrQuarantine<ZetlPendingProjectRemovalFile>(markerPath, log);
        if (marker is { } committedMarker
            && string.Equals(
                committedMarker.Status,
                CommittedRemovalStatus,
                StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.Delete(pendingDirectory, recursive: true);
                log?.Invoke(
                    $"Completed interrupted removal of project '{committedMarker.ProjectId}'.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke(
                    $"Could not clean committed project removal '{pendingDirectory}': {ex.Message}");
            }

            return;
        }

        // Prepared, missing, unreadable, and unknown markers all restore. The
        // workspace is not a complete project index, so it cannot safely prove
        // that an uncommitted directory should be destroyed.
        var pendingName = Path.GetFileName(pendingDirectory);
        var originalFolder = ValidProjectFolder(marker?.OriginalFolder)
            ?? ValidProjectFolder(InferOriginalFolder(pendingName));
        if (originalFolder is null)
        {
            log?.Invoke(
                $"Could not recover pending project removal '{pendingDirectory}': its original folder is unknown.");
            return;
        }

        var projectDirectory = Path.Combine(projectsDirectory, originalFolder);
        if (Directory.Exists(projectDirectory))
        {
            log?.Invoke(
                $"Could not recover pending project removal '{pendingDirectory}': '{projectDirectory}' already exists.");
            return;
        }

        try
        {
            Directory.CreateDirectory(projectsDirectory);
            Directory.Move(pendingDirectory, projectDirectory);
            DeleteRemovalMarker(projectDirectory);
            log?.Invoke(
                $"Restored interrupted removal of project '{marker?.ProjectId ?? originalFolder}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke(
                $"Could not restore pending project removal '{pendingDirectory}': {ex.Message}");
        }
    }

    private static string? InferOriginalFolder(string pendingName)
    {
        var separator = pendingName.LastIndexOf('.');
        return separator > 0
            && Guid.TryParseExact(pendingName[(separator + 1)..], "N", out _)
                ? pendingName[..separator]
                : null;
    }

    private static string? ValidProjectFolder(string? folder)
    {
        return !string.IsNullOrWhiteSpace(folder)
            && string.Equals(folder, Path.GetFileName(folder), StringComparison.Ordinal)
            && folder is not "." and not ".."
                ? folder
                : null;
    }

    private static void DeleteRemovalMarker(string projectDirectory)
    {
        var markerPath = Path.Combine(projectDirectory, PendingRemovalMarkerFileName);
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
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

internal sealed class ZetlPendingProjectRemovalFile
{
    public int Version { get; set; } = 1;

    public string ProjectId { get; set; } = "";

    public string OriginalFolder { get; set; } = "";

    public string Status { get; set; } = ZetlStateStorage.PreparedRemovalStatus;
}
