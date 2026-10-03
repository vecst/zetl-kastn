using System.Runtime.CompilerServices;
using ZETL.Contracts;
using static ZETL.ZetlStateRules;

namespace ZETL;

// The one owner of Zetl's data: every change goes through here, under one
// lock, and is written in the order the persistence coordinator commits. This
// file holds the lock, the lanes' active project and bucket, and the writes;
// the rest is split by area across ZetlStateStore.Journal, .Projects,
// .Buckets, .Slips, .Replay, and .Normalization.
internal sealed partial class ZetlStateStore
{
    private readonly IZetlProjectStorage projectStorage;
    private readonly IZetlProjectDirectoryLifecycle projectDirectories;
    private readonly ZetlStatePersistenceCoordinator persistence;
    private readonly string sessionId;
    private readonly Action<string>? log;

    // Current UI/runtime mutations and the Kastn command service share this
    // instance monitor. Synchronized public mutators therefore cannot interleave
    // their in-memory changes before an atomic project write.
    internal object MutationSyncRoot => this;

    public ZetlStateStore(string? statePath = null, string? sessionId = null, Action<string>? log = null)
        : this(CreateStorage(statePath, log), sessionId, log)
    {
    }

    internal ZetlStateStore(
        IZetlStateStorage storage,
        string? sessionId = null,
        Action<string>? log = null)
        : this(storage, storage, storage, storage, sessionId, log)
    {
    }

    internal ZetlStateStore(
        IZetlStateLoader stateLoader,
        IZetlProjectStorage projectStorage,
        IZetlWorkspaceStorage workspaceStorage,
        IZetlProjectDirectoryLifecycle projectDirectories,
        string? sessionId = null,
        Action<string>? log = null)
    {
        this.projectStorage = projectStorage;
        this.projectDirectories = projectDirectories;
        this.log = log;
        this.sessionId = string.IsNullOrWhiteSpace(sessionId) ? NewId() : sessionId;
        State = stateLoader.Load();
        persistence = new ZetlStatePersistenceCoordinator(
            projectStorage,
            workspaceStorage,
            projectDirectories,
            State,
            log);
        NormalizeLoadedState();
        // Loaded-state normalization may discard abandoned temporary projects.
        // Once that startup repair succeeds, its normalized shape becomes the
        // baseline for later compensating writes, matching the pre-extraction
        // constructor contract.
        persistence.ResetBaseline(State);
    }

    private static IZetlStateStorage CreateStorage(string? statePath, Action<string>? log)
    {
        // Historically callers passed a single state.json path. Storage is now a
        // directory layout, so treat that path's directory as the workspace root
        // and offer the file itself up for one-time migration.
        var legacyStatePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Zetl",
            "state.json");
        var rootDirectory = Path.GetDirectoryName(legacyStatePath);
        if (string.IsNullOrEmpty(rootDirectory))
        {
            rootDirectory = Directory.GetCurrentDirectory();
        }

        return new ZetlStateStorage(rootDirectory, legacyStatePath, log);
    }

    public ZetlState State { get; private set; }

    // Defaults applied to newly created projects and buckets. Set from app
    // settings; falls back to the built-in Standard defaults.
    public ZetlBucketDefaults Defaults { get; set; } = ZetlBucketDefaults.Standard;

    public string SessionId => sessionId;

    public event EventHandler? Changed;

    public event EventHandler<ZetlProjectPersistedEventArgs>? ProjectPersisted;

    public ZetlProject? ActiveProject => State.Projects.FirstOrDefault(project => project.Id == State.ActiveProjectId);

    public ZetlProject? ShiftActiveProject => GetActiveProject(shifted: true);

    public ZetlBucket? ActiveBucket
    {
        get
        {
            return GetActiveBucket(shifted: false);
        }
    }

    public ZetlBucket? ShiftActiveBucket => GetActiveBucket(shifted: true);

    public ZetlProject? GetActiveProject(bool shifted = false)
    {
        var activeProjectId = shifted ? State.ShiftActiveProjectId : State.ActiveProjectId;
        return State.Projects.FirstOrDefault(project => project.Id == activeProjectId);
    }

    public ZetlBucket? GetActiveBucket(bool shifted = false)
    {
        var project = GetActiveProject(shifted);
        return project?.Buckets.FirstOrDefault(bucket =>
            bucket.Id == project.ActiveBucketId && !IsDeletedBucket(bucket));
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetActiveProject(string projectId, bool shifted = false)
    {
        if (State.Projects.Any(project => project.Id == projectId))
        {
            SetActiveProjectId(projectId, shifted);
            PersistWorkspace();
            DisposeInactiveTemporaryProjects(persistWorkspace: true);
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetActiveBucket(ZetlProject project, string bucketId)
    {
        if (project.Buckets.Any(bucket => bucket.Id == bucketId && !IsDeletedBucket(bucket)))
        {
            project.ActiveBucketId = bucketId;
            PersistProject(project);
        }
    }

    // Persist a single project's file, optionally rewriting the workspace
    // pointers alongside it. Note capture, edits, and toggles all touch exactly
    // one project, so scoping the save here keeps each write to the one file
    // that changed instead of rewriting every project on disk.
    private void PersistProject(ZetlProject project, bool workspace = false)
    {
        try
        {
            persistence.PersistProject(
                State,
                project,
                workspace,
                NormalizeProject,
                NormalizeWorkspacePointers,
                BuildWorkspaceFile);
        }
        catch
        {
            RestoreDurableState();
            throw;
        }

        RaiseProjectPersisted(
            new ZetlProjectPersistedEventArgs(project.Id, project.ChangeSequence));
        RaiseChanged();
    }

    // Persist the project that owns the given bucket. Bucket- and note-level
    // mutations don't carry their project, so we resolve the owner here.
    private void PersistBucket(ZetlBucket bucket)
    {
        var owner = OwnerProject(bucket);
        if (owner is null)
        {
            SaveAll();
            return;
        }

        PersistProject(owner);
    }

    private void PersistSlip(ZetlSlip note)
    {
        var owner = OwnerProjectOfSlip(note);
        if (owner is null)
        {
            SaveAll();
            return;
        }

        PersistProject(owner);
    }

    private void PersistWorkspace()
    {
        try
        {
            persistence.PersistWorkspace(
                State,
                NormalizeWorkspacePointers,
                BuildWorkspaceFile);
        }
        catch
        {
            RestoreDurableState();
            throw;
        }

        RaiseChanged();
    }

    // Full flush: every project plus the workspace pointers. Used as a safety
    // net when a mutation can't resolve which project it touched.
    private void SaveAll()
    {
        try
        {
            persistence.SaveAll(
                State,
                NormalizeProject,
                NormalizeWorkspacePointers,
                BuildWorkspaceFile);
        }
        catch
        {
            RestoreDurableState();
            throw;
        }

        RaiseChanged();
    }

    private void RestoreDurableState()
    {
        State = persistence.RestoreState();
        RaiseChanged();
    }

    private void RaiseProjectPersisted(ZetlProjectPersistedEventArgs args)
    {
        ZetlEventPublisher.Publish(
            ProjectPersisted,
            this,
            args,
            ex => log?.Invoke($"Project persistence subscriber failed: {ex.Message}"));
    }

    private void RaiseChanged()
    {
        ZetlEventPublisher.Publish(
            Changed,
            this,
            EventArgs.Empty,
            ex => log?.Invoke($"State change subscriber failed: {ex.Message}"));
    }

    private ZetlProject? OwnerProject(ZetlBucket bucket)
    {
        return State.Projects.FirstOrDefault(project => project.Buckets.Any(item => item.Id == bucket.Id));
    }

    private ZetlProject? OwnerProjectOfSlip(ZetlSlip note)
    {
        return State.Projects.FirstOrDefault(project =>
            project.Buckets.Any(bucket => bucket.Slips.Any(item => item.Id == note.Id)));
    }

    private static ZetlWorkspaceFile BuildWorkspaceFile(ZetlState state)
    {
        return new ZetlWorkspaceFile
        {
            Version = Math.Max(state.Version, 1),
            ActiveProjectId = state.ActiveProjectId,
            ShiftActiveProjectId = state.ShiftActiveProjectId,
            DefaultJournalProjectId = state.DefaultJournalProjectId,
            ShiftDefaultJournalProjectId = state.ShiftDefaultJournalProjectId,
            LastDeliberateProjectId = state.LastDeliberateProjectId,
            ShiftLastDeliberateProjectId = state.ShiftLastDeliberateProjectId
        };
    }

    private static string NormalizeName(string? value, string fallback)
    {
        var normalized = (value ?? "").Trim();
        return normalized.Length == 0 ? fallback : normalized;
    }

    private bool IsCurrentSessionSlip(ZetlSlip note)
    {
        return string.Equals(note.SessionId, sessionId, StringComparison.Ordinal);
    }

    private void SetActiveProjectId(string? projectId, bool shifted)
    {
        if (shifted)
        {
            State.ShiftActiveProjectId = projectId;
        }
        else
        {
            State.ActiveProjectId = projectId;
        }

        // Activating (or reactivating) a deliberate project refreshes its auto-return
        // window and records it as the lane's last deliberate project (for Ctrl+A).
        if (projectId is not null
            && State.Projects.FirstOrDefault(project => project.Id == projectId) is { } activated)
        {
            TouchProjectActivity(activated);
            if (!activated.JournalMode && !IsConsumableProject(activated))
            {
                if (shifted)
                {
                    State.ShiftLastDeliberateProjectId = activated.Id;
                }
                else
                {
                    State.LastDeliberateProjectId = activated.Id;
                }
            }
        }
    }

    private static string NewId()
    {
        return Guid.NewGuid().ToString("N");
    }
}
