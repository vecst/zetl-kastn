using ZETL.Contracts;
using static ZETL.ZetlStateRules;

namespace ZETL;

// Projects: creating, renaming, finishing, deleting, consolidating, and the
// queries that find the latest one.
internal sealed partial class ZetlStateStore
{
    public ZetlProject CreateProject(
        string name,
        IEnumerable<string> bucketNames,
        string? activeBucketName = null,
        bool shifted = false,
        string? kind = null,
        string? sourceTemplateId = null,
        string? temporaryLane = null,
        bool consumable = false)
    {
        lock (stateGate)
        {
            var normalizedName = NormalizeName(name, "Untitled Project");
            if (string.Equals(normalizedName, DefaultProjectName(shifted), StringComparison.OrdinalIgnoreCase))
            {
                var existingDefaultProject = ConsolidateProjectsNamed(normalizedName);
                if (existingDefaultProject is not null)
                {
                    EnsureBuckets(existingDefaultProject, bucketNames);
                    var existingActiveBucket = existingDefaultProject.Buckets.FirstOrDefault(bucket =>
                        string.Equals(bucket.Name, activeBucketName, StringComparison.OrdinalIgnoreCase));
                    if (existingActiveBucket is not null)
                    {
                        existingDefaultProject.ActiveBucketId = existingActiveBucket.Id;
                    }

                    SetActiveProjectId(existingDefaultProject.Id, shifted);
                    PersistProject(existingDefaultProject, workspace: true);
                    return existingDefaultProject;
                }
            }

            var buckets = NormalizeBucketNames(bucketNames)
                .Select(CreateBucket)
                .ToList();
            EnsureScratchBucket(buckets);
            foreach (var bucket in buckets)
            {
                ApplyBucketDefaults(bucket);
            }

            var activeBucket = buckets.FirstOrDefault(bucket =>
                string.Equals(bucket.Name, activeBucketName, StringComparison.OrdinalIgnoreCase))
                ?? buckets.First();

            var project = new ZetlProject
            {
                Id = NewId(),
                Name = normalizedName,
                Kind = NormalizeProjectKind(kind),
                SourceTemplateId = string.IsNullOrWhiteSpace(sourceTemplateId) ? null : sourceTemplateId.Trim(),
                TemporaryLane = CanonicalTemporaryLane(temporaryLane) ?? (shifted ? ShiftLane : NormalLane),
                ActiveBucketId = activeBucket.Id,
                Buckets = buckets
            };
            if (!IsTemporaryConsumableProject(project))
            {
                project.SourceTemplateId = null;
                project.TemporaryLane = null;
            }

            project.Consumable = consumable || IsTemporaryConsumableProject(project);
            if (project.Consumable)
            {
                project.ReturnProjectId = shifted ? State.ShiftActiveProjectId : State.ActiveProjectId;
            }

            State.Projects.Add(project);
            SetActiveProjectId(project.Id, shifted);
            PersistProject(project, workspace: true);
            DisposeInactiveTemporaryProjects(persistWorkspace: true);
            return project;
        }
    }

    public void ConsolidateDefaultProject(bool shifted = false)
    {
        lock (stateGate)
        {
            // ConsolidateProjectsNamed persists the merged project and removes the
            // duplicates' files itself, so there is nothing extra to save here.
            ConsolidateProjectsNamed(DefaultProjectName(shifted));
        }
    }

    public void UpdateProjectName(ZetlProject project, string name, bool shifted = false)
    {
        lock (stateGate)
        {
            project.Name = NormalizeName(name, DefaultProjectName(shifted));
            project.MetadataRevision++;
            PersistProject(project);
        }
    }

    public void SetProjectStatus(ZetlProject project, string status)
    {
        lock (stateGate)
        {
            project.Status = NormalizeProjectStatus(status);
            project.MetadataRevision++;

            // Invariant: a non-Active project is never lane-active. Whether a project
            // is sealed via Zetl's Finish button or archived from Kastn, it leaves its
            // lane so capture advances instead of landing in a put-away project.
            var clearedLane = false;
            if (!IsActiveStatus(project))
            {
                if (State.ActiveProjectId == project.Id)
                {
                    State.ActiveProjectId = null;
                    clearedLane = true;
                }

                if (State.ShiftActiveProjectId == project.Id)
                {
                    State.ShiftActiveProjectId = null;
                    clearedLane = true;
                }
            }

            PersistProject(project, workspace: clearedLane);
            if (clearedLane)
            {
                DisposeInactiveTemporaryProjects(persistWorkspace: true);
            }
        }
    }

    public void SetProjectDefaultView(ZetlProject project, string? viewId)
    {
        lock (stateGate)
        {
            project.DefaultViewId = string.IsNullOrWhiteSpace(viewId) ? null : viewId.Trim();
            project.MetadataRevision++;
            PersistProject(project);
        }
    }

    public void SaveProjectView(ZetlProject project, ZetlViewDocument view)
    {
        lock (stateGate)
        {
            var index = project.Views.FindIndex(item =>
                string.Equals(item.Id, view.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                project.Views[index] = ZetlViewDefaults.Clone(view);
            }
            else
            {
                project.Views.Add(ZetlViewDefaults.Clone(view));
            }

            project.MetadataRevision++;
            PersistProject(project);
        }
    }

    public void DeleteProjectView(ZetlProject project, string viewId)
    {
        lock (stateGate)
        {
            project.Views.RemoveAll(view => string.Equals(view.Id, viewId, StringComparison.Ordinal));
            if (string.Equals(project.DefaultViewId, viewId, StringComparison.Ordinal))
            {
                project.DefaultViewId = null;
            }

            project.MetadataRevision++;
            PersistProject(project);
        }
    }

    public void ClearActiveProject(bool shifted = false)
    {
        lock (stateGate)
        {
            SetActiveProjectId(null, shifted);
            PersistWorkspace();
            DisposeInactiveTemporaryProjects(persistWorkspace: true);
        }
    }

    // Seal a project by id: mark it Finished and clear it from whichever lane(s)
    // it occupies, so the next capture advances to a fresh dated session. Used by
    // the compile dialog, whose source project may be any project (not just the
    // current lane's). No-op (returns null) for an unknown id.
    public ZetlProject? FinishProject(string projectId)
    {
        lock (stateGate)
        {
            var project = State.Projects.FirstOrDefault(item => item.Id == projectId);
            if (project is null)
            {
                return null;
            }

            // SetProjectStatus owns the lane-clearing invariant for any non-Active
            // status, so finishing is just sealing to Finished.
            SetProjectStatus(project, FinishedStatus);
            return project;
        }
    }

    public void DeleteProject(string projectId)
    {
        lock (stateGate)
        {
            var project = State.Projects.FirstOrDefault(item => item.Id == projectId);
            if (project is null)
            {
                return;
            }

            State.Projects.Remove(project);
            IZetlProjectRemoval? removal = null;
            try
            {
                removal = projectDirectories.PrepareProjectRemoval(projectId);
                if (State.ActiveProjectId == projectId)
                {
                    State.ActiveProjectId = State.Projects.FirstOrDefault()?.Id;
                }

                if (State.ShiftActiveProjectId == projectId)
                {
                    State.ShiftActiveProjectId = State.Projects.FirstOrDefault()?.Id;
                }

                PersistWorkspace();
                removal.Commit();
            }
            catch
            {
                if (removal is not null)
                {
                    try
                    {
                        removal.RollBack();
                    }
                    catch (Exception rollbackError)
                    {
                        log?.Invoke(
                            $"Could not restore project directory '{projectId}' after a failed delete: {rollbackError.Message}");
                    }
                }

                persistence.RollBackProjectFile(projectId);
                RestoreDurableState();
                throw;
            }
            finally
            {
                removal?.Dispose();
            }

            persistence.CommitProjectRemoval(projectId);
        }
    }

    // The project most recently written to (its latest note), ignoring the
    // Zetl Logs infrastructure project, which is appended to constantly. Used to
    // land the Board on the last project you actually touched when no project is
    // active.
    public ZetlProject? GetMostRecentlyWrittenProject()
    {
        return ProjectsByLatestWrite()
            .Where(item => item.Latest is not null)
            .Select(item => item.Project)
            .FirstOrDefault();
    }

    // The project a held Ctrl+V compiles when no project is active: the most
    // recently written one that has text to compile. Consumable projects are
    // queues to paste out one at a time, not notes to compile, so they only
    // compile from here while active.
    public ZetlProject? GetMostRecentCompilableProject()
    {
        return ProjectsByLatestWrite()
            .Where(item => item.Latest is not null)
            .Select(item => item.Project)
            .FirstOrDefault(project =>
                !IsConsumableProject(project)
                // Consumables kept before projects were marked show only
                // through their Replay bucket.
                && !project.Buckets.Any(IsReplayBucket)
                && HasCompilableSlips(project));
    }

    // Every project, most recently written first. Projects without notes, and
    // Zetl Logs, follow by name.
    public IReadOnlyList<ZetlProject> GetProjectsByRecentWrite()
    {
        return ProjectsByLatestWrite().Select(item => item.Project).ToList();
    }

    private IEnumerable<(ZetlProject Project, DateTimeOffset? Latest)> ProjectsByLatestWrite()
    {
        return State.Projects
            .Select(project => (
                Project: project,
                Latest: string.Equals(project.Name, LogProjectName, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : project.Buckets
                        .Where(bucket => !IsDeletedBucket(bucket))
                        .SelectMany(bucket => bucket.Slips)
                        .Select(note => (DateTimeOffset?)note.CreatedAtUtc)
                        .Max()))
            .OrderByDescending(item => item.Latest is not null)
            .ThenByDescending(item => item.Latest)
            .ThenBy(item => item.Project.Name, StringComparer.OrdinalIgnoreCase);
    }

    private bool DisposeInactiveTemporaryProjects(bool persistWorkspace)
    {
        var removed = false;
        var removedProjectIds = new List<string>();
        try
        {
            foreach (var project in State.Projects.Where(IsTemporaryConsumableProject).ToList())
            {
                var lane = CanonicalTemporaryLane(project.TemporaryLane);
                if (lane is null)
                {
                    lane = string.Equals(State.ActiveProjectId, project.Id, StringComparison.Ordinal)
                        ? NormalLane
                        : string.Equals(State.ShiftActiveProjectId, project.Id, StringComparison.Ordinal)
                            ? ShiftLane
                            : null;
                    project.TemporaryLane = lane;
                }

                var laneActiveId = string.Equals(lane, ShiftLane, StringComparison.Ordinal)
                    ? State.ShiftActiveProjectId
                    : State.ActiveProjectId;
                if (lane is not null
                    && string.Equals(laneActiveId, project.Id, StringComparison.Ordinal)
                    && IsActiveStatus(project))
                {
                    if (string.Equals(lane, ShiftLane, StringComparison.Ordinal)
                        && string.Equals(State.ActiveProjectId, project.Id, StringComparison.Ordinal))
                    {
                        State.ActiveProjectId = null;
                        removed = true;
                    }

                    if (string.Equals(lane, NormalLane, StringComparison.Ordinal)
                        && string.Equals(State.ShiftActiveProjectId, project.Id, StringComparison.Ordinal))
                    {
                        State.ShiftActiveProjectId = null;
                        removed = true;
                    }

                    continue;
                }

                removedProjectIds.Add(project.Id);
                RemoveTemporaryProject(project);
                removed = true;
            }

            if (removed && persistWorkspace)
            {
                PersistWorkspace();
                foreach (var projectId in removedProjectIds)
                {
                    persistence.CommitProjectRemoval(projectId);
                }
            }
        }
        catch
        {
            foreach (var projectId in removedProjectIds)
            {
                persistence.RollBackProjectFile(projectId);
            }
            RestoreDurableState();
            throw;
        }

        return removed;
    }

    private void RemoveTemporaryProject(ZetlProject project)
    {
        var projectId = project.Id;
        State.Projects.Remove(project);
        projectDirectories.RemoveProject(projectId);
        if (State.ActiveProjectId == projectId)
        {
            State.ActiveProjectId = null;
        }

        if (State.ShiftActiveProjectId == projectId)
        {
            State.ShiftActiveProjectId = null;
        }

        if (State.LastDeliberateProjectId == projectId)
        {
            State.LastDeliberateProjectId = null;
        }

        if (State.ShiftLastDeliberateProjectId == projectId)
        {
            State.ShiftLastDeliberateProjectId = null;
        }

        if (State.DefaultJournalProjectId == projectId)
        {
            State.DefaultJournalProjectId = null;
        }

        if (State.ShiftDefaultJournalProjectId == projectId)
        {
            State.ShiftDefaultJournalProjectId = null;
        }

        log?.Invoke($"Disposed temporary consumable project '{project.Name}'.");
    }

    private ZetlProject? ConsolidateProjectsNamed(string projectName)
    {
        // Only ever consolidate Active sessions. A finished/archived project that
        // happens to share a name must stay standalone, so finishing is never
        // silently undone by a later merge.
        var matchingProjects = State.Projects
            .Where(project => string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase)
                && IsActiveStatus(project))
            .ToList();
        if (matchingProjects.Count == 0)
        {
            return null;
        }

        var primary = matchingProjects[0];
        var merged = false;
        foreach (var duplicate in matchingProjects.Skip(1))
        {
            CopyImageAssets(primary, duplicate);
            MergeProjectInto(primary, duplicate);
            State.Projects.Remove(duplicate);
            projectDirectories.RemoveProject(duplicate.Id);
            merged = true;
        }

        // Persist the merged result (and the duplicates' removals) here so every
        // caller of consolidation lands the same on-disk state, even those that
        // don't otherwise save.
        if (merged)
        {
            PersistProject(primary);
        }

        return primary;
    }

    private void CopyImageAssets(ZetlProject targetProject, ZetlProject sourceProject)
    {
        foreach (var note in sourceProject.Buckets
            .SelectMany(bucket => bucket.Slips)
            .Where(note => note.Image is not null))
        {
            var bytes = projectStorage.ReadAsset(sourceProject, note.Image!.RelativePath);
            if (bytes is null)
            {
                continue;
            }

            var extension = Path.GetExtension(note.Image.RelativePath);
            note.Image.RelativePath = projectStorage.WriteAsset(
                targetProject,
                note.Image.Sha256,
                string.IsNullOrWhiteSpace(extension) ? ".png" : extension,
                bytes);
        }
    }

    private static void MergeProjectInto(ZetlProject targetProject, ZetlProject sourceProject)
    {
        var bucketMap = new Dictionary<string, ZetlBucket>(StringComparer.Ordinal);
        // Merge parents before their children so a child's parent is already
        // mapped when we resolve its ParentBucketId; otherwise a child listed
        // ahead of its parent would lose its parent link and become top-level.
        foreach (var sourceBucket in OrderParentsFirst(sourceProject))
        {
            var parentBucket = sourceBucket.ParentBucketId is not null && bucketMap.TryGetValue(sourceBucket.ParentBucketId, out var mappedParent)
                ? mappedParent
                : null;
            var targetBucket = targetProject.Buckets.FirstOrDefault(bucket =>
                string.Equals(bucket.Name, sourceBucket.Name, StringComparison.OrdinalIgnoreCase)
                && bucket.ParentBucketId == parentBucket?.Id);
            var sourceKind = NormalizeBucketKind(sourceBucket.Settings.Kind);
            if (targetBucket is null)
            {
                targetBucket = new ZetlBucket
                {
                    Id = NewId(),
                    Name = NormalizeName(sourceBucket.Name, "Bucket"),
                    ParentBucketId = parentBucket?.Id,
                    Settings = new ZetlBucketSettings
                    {
                        Kind = sourceKind,
                        DefaultKind = NormalizeBucketKind(sourceBucket.Settings.DefaultKind),
                        DefaultCompileMode = NormalizeCompileMode(sourceBucket.Settings.DefaultCompileMode),
                        DefaultStartingText = (sourceBucket.Settings.DefaultStartingText ?? "").Trim(),
                        DefaultTsvRowLength = sourceBucket.Settings.DefaultTsvRowLength <= 0 ? 5 : sourceBucket.Settings.DefaultTsvRowLength
                    },
                    Slips = new List<ZetlSlip>()
                };
                targetProject.Buckets.Add(targetBucket);
            }
            else
            {
                if (IsReplayKindValue(sourceKind))
                {
                    targetBucket.Settings.Kind = sourceKind;
                }

                targetBucket.Settings.DefaultCompileMode = NormalizeCompileMode(sourceBucket.Settings.DefaultCompileMode);
                if (string.IsNullOrWhiteSpace(targetBucket.Settings.DefaultStartingText))
                {
                    targetBucket.Settings.DefaultStartingText = (sourceBucket.Settings.DefaultStartingText ?? "").Trim();
                }

                targetBucket.Settings.DefaultTsvRowLength = sourceBucket.Settings.DefaultTsvRowLength <= 0 ? 5 : sourceBucket.Settings.DefaultTsvRowLength;
            }

            foreach (var note in sourceBucket.Slips)
            {
                targetBucket.Slips.Add(note);
            }

            bucketMap[sourceBucket.Id] = targetBucket;
        }

        foreach (var sourceBucket in sourceProject.Buckets)
        {
            if (sourceBucket.Settings.ReplayReviewBucketId is not null
                && bucketMap.TryGetValue(sourceBucket.Id, out var targetBucket)
                && bucketMap.TryGetValue(sourceBucket.Settings.ReplayReviewBucketId, out var targetReviewBucket)
                && targetBucket.Id != targetReviewBucket.Id)
            {
                targetBucket.Settings.ReplayReviewBucketId = targetReviewBucket.Id;
            }

            if (sourceBucket.Settings.PassThroughReviewBucketId is not null
                && bucketMap.TryGetValue(sourceBucket.Id, out targetBucket)
                && bucketMap.TryGetValue(sourceBucket.Settings.PassThroughReviewBucketId, out targetReviewBucket)
                && targetBucket.Id != targetReviewBucket.Id)
            {
                targetBucket.Settings.PassThroughReviewBucketId = targetReviewBucket.Id;
            }
        }

        if (targetProject.QuickNoteBucketId is null
            && sourceProject.QuickNoteBucketId is not null
            && bucketMap.TryGetValue(sourceProject.QuickNoteBucketId, out var quickNoteBucket))
        {
            targetProject.QuickNoteBucketId = quickNoteBucket.Id;
        }
    }
}
