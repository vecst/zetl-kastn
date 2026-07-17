namespace ZETL;

/// <summary>
/// Owns the durable-state baseline and the ordering of project/workspace writes,
/// commits, and compensating writes. Domain mutations remain in ZetlStateStore;
/// this component only decides when a durable baseline may advance.
/// </summary>
internal sealed class ZetlStatePersistenceCoordinator
{
    private readonly IZetlProjectStorage projectStorage;
    private readonly IZetlWorkspaceStorage workspaceStorage;
    private readonly IZetlProjectDirectoryLifecycle projectDirectories;
    private readonly Action<string>? log;
    private ZetlState durableState;

    public ZetlStatePersistenceCoordinator(
        IZetlProjectStorage projectStorage,
        IZetlWorkspaceStorage workspaceStorage,
        IZetlProjectDirectoryLifecycle projectDirectories,
        ZetlState initialState,
        Action<string>? log = null)
    {
        this.projectStorage = projectStorage;
        this.workspaceStorage = workspaceStorage;
        this.projectDirectories = projectDirectories;
        this.log = log;
        durableState = JsonFile.Clone(initialState);
    }

    public void PersistProject(
        ZetlState state,
        ZetlProject project,
        bool includeWorkspace,
        Action<ZetlProject> normalizeProject,
        Action normalizeWorkspace,
        Func<ZetlState, ZetlWorkspaceFile> buildWorkspace)
    {
        var previousSequence = project.ChangeSequence;
        var projectWriteAttempted = false;
        var workspaceWriteAttempted = false;
        try
        {
            normalizeProject(project);
            project.ChangeSequence = Math.Max(previousSequence, 0) + 1;
            projectWriteAttempted = true;
            projectStorage.WriteProject(project);

            if (includeWorkspace)
            {
                normalizeWorkspace();
                workspaceWriteAttempted = true;
                workspaceStorage.WriteWorkspace(buildWorkspace(state));
            }
        }
        catch
        {
            project.ChangeSequence = previousSequence;
            if (projectWriteAttempted)
            {
                RollBackProjectFile(project.Id);
            }

            if (workspaceWriteAttempted)
            {
                RollBackWorkspaceFile(buildWorkspace);
            }

            throw;
        }

        CommitProject(project);
        if (includeWorkspace)
        {
            CommitWorkspace(state);
        }
    }

    public void PersistWorkspace(
        ZetlState state,
        Action normalizeWorkspace,
        Func<ZetlState, ZetlWorkspaceFile> buildWorkspace)
    {
        try
        {
            normalizeWorkspace();
            workspaceStorage.WriteWorkspace(buildWorkspace(state));
        }
        catch
        {
            RollBackWorkspaceFile(buildWorkspace);
            throw;
        }

        CommitWorkspace(state);
    }

    public void SaveAll(
        ZetlState state,
        Action<ZetlProject> normalizeProject,
        Action normalizeWorkspace,
        Func<ZetlState, ZetlWorkspaceFile> buildWorkspace)
    {
        var attemptedProjectIds = new List<string>();
        var workspaceWriteAttempted = false;
        try
        {
            foreach (var project in state.Projects)
            {
                normalizeProject(project);
                attemptedProjectIds.Add(project.Id);
                projectStorage.WriteProject(project);
            }

            normalizeWorkspace();
            workspaceWriteAttempted = true;
            workspaceStorage.WriteWorkspace(buildWorkspace(state));
        }
        catch
        {
            foreach (var projectId in attemptedProjectIds)
            {
                RollBackProjectFile(projectId);
            }

            if (workspaceWriteAttempted)
            {
                RollBackWorkspaceFile(buildWorkspace);
            }

            throw;
        }

        durableState = JsonFile.Clone(state);
    }

    public ZetlState RestoreState() => JsonFile.Clone(durableState);

    public void ResetBaseline(ZetlState state)
    {
        durableState = JsonFile.Clone(state);
    }

    public void CommitProjectRemoval(string projectId)
    {
        durableState.Projects.RemoveAll(project => project.Id == projectId);
    }

    public void RollBackProjectFile(string projectId)
    {
        if (durableState.Projects.FirstOrDefault(project => project.Id == projectId) is { } durableProject)
        {
            try
            {
                projectStorage.WriteProject(durableProject);
            }
            catch (Exception rollbackError)
            {
                log?.Invoke(
                    $"Could not roll back project '{projectId}' after a failed write: {rollbackError.Message}");
            }

            return;
        }

        try
        {
            projectDirectories.RemoveProject(projectId);
        }
        catch (Exception rollbackError)
        {
            log?.Invoke(
                $"Could not remove partially written project '{projectId}': {rollbackError.Message}");
        }
    }

    private void CommitProject(ZetlProject project)
    {
        var snapshot = JsonFile.Clone(project);
        var index = durableState.Projects.FindIndex(candidate => candidate.Id == project.Id);
        if (index >= 0)
        {
            durableState.Projects[index] = snapshot;
        }
        else
        {
            durableState.Projects.Add(snapshot);
        }
    }

    private void CommitWorkspace(ZetlState state)
    {
        durableState.Version = state.Version;
        durableState.ActiveProjectId = state.ActiveProjectId;
        durableState.ShiftActiveProjectId = state.ShiftActiveProjectId;
        durableState.DefaultJournalProjectId = state.DefaultJournalProjectId;
        durableState.ShiftDefaultJournalProjectId = state.ShiftDefaultJournalProjectId;
        durableState.LastDeliberateProjectId = state.LastDeliberateProjectId;
        durableState.ShiftLastDeliberateProjectId = state.ShiftLastDeliberateProjectId;
    }

    private void RollBackWorkspaceFile(Func<ZetlState, ZetlWorkspaceFile> buildWorkspace)
    {
        try
        {
            workspaceStorage.WriteWorkspace(buildWorkspace(durableState));
        }
        catch (Exception rollbackError)
        {
            log?.Invoke(
                $"Could not roll back workspace pointers after a failed write: {rollbackError.Message}");
        }
    }
}
