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
internal sealed class ZetlStateStorage
{
    private readonly string workspacePath;
    private readonly string projectsDirectory;
    private readonly string? legacyStatePath;

    // project id -> folder name under projectsDirectory. Rebuilt on load and
    // extended as new projects are written.
    private readonly Dictionary<string, string> projectFolders = new(StringComparer.Ordinal);

    public ZetlStateStorage(string rootDirectory, string? legacyStatePath)
    {
        workspacePath = Path.Combine(rootDirectory, "workspace.json");
        projectsDirectory = Path.Combine(rootDirectory, "projects");
        this.legacyStatePath = legacyStatePath;
    }

    public ZetlState Load()
    {
        MigrateLegacyStateIfNeeded();

        var workspace = JsonFile.Read<ZetlWorkspaceFile>(workspacePath) ?? new ZetlWorkspaceFile();
        var state = new ZetlState
        {
            Version = workspace.Version,
            ActiveProjectId = workspace.ActiveProjectId,
            ShiftActiveProjectId = workspace.ShiftActiveProjectId,
            Projects = ReadProjects()
        };
        return state;
    }

    public void WriteProject(ZetlProject project)
    {
        var folder = GetOrAssignFolder(project);
        JsonFile.WriteAtomic(Path.Combine(projectsDirectory, folder, "project.json"), project);
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
            var project = JsonFile.Read<ZetlProject>(Path.Combine(directory, "project.json"));
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

        var legacy = JsonFile.Read<ZetlState>(legacyStatePath);
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
            ShiftActiveProjectId = legacy.ShiftActiveProjectId
        });

        // Keep the original file as a backup rather than deleting it outright.
        var backupPath = legacyStatePath + ".bak";
        File.Move(legacyStatePath, backupPath, overwrite: true);
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

/// <summary>
/// Serialized shape of <c>workspace.json</c>: the store-wide version and the
/// two lanes' active-project pointers. Projects themselves live in their own
/// files, so they are intentionally absent here.
/// </summary>
internal sealed class ZetlWorkspaceFile
{
    public int Version { get; set; } = 1;
    public string? ActiveProjectId { get; set; }
    public string? ShiftActiveProjectId { get; set; }
}
