using System.Runtime.CompilerServices;
using ZETL.Contracts;

namespace ZETL;

internal sealed class ZetlStateStore
{
    // Name of the dedicated activity-log project. It is never made active.
    public const string LogProjectName = "Zetl Logs";
    public const string DeletedBucketName = "Deleted";
    public const string DeletedBucketKind = "Deleted";

    // Scratch is identified by its name, which is sound only while no other
    // bucket can take that name: see IsReservedBucketName.
    public const string ScratchBucketName = "Scratch";

    // A journal day is a parent bucket (named e.g. "Mon 07-06") holding two lazily
    // created children: Capture receives copy captures, Quick Note receives held-cut
    // quick notes. They are ordinary buckets with special routing, not protected like
    // the singular Scratch/Deleted (a journal has one pair per day).
    public const string JournalCaptureBucketName = "Capture";
    public const string JournalQuickNoteBucketName = "Quick Note";

    // Project lifecycle statuses. Stored as readable words, matching the bucket
    // Kind / compile-mode string convention.
    public const string ActiveStatus = "Active";
    public const string FinishedStatus = "Finished";
    public const string ArchivedStatus = "Archived";
    public const string StandardProjectKind = "Standard";
    public const string TemporaryConsumableProjectKind = "TemporaryConsumable";
    public const string NormalLane = "Normal";
    public const string ShiftLane = "Shift";

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
    public ZetlProject CreateProject(
        string name,
        IEnumerable<string> bucketNames,
        string? activeBucketName = null,
        bool shifted = false,
        string? kind = null,
        string? sourceTemplateId = null,
        string? temporaryLane = null)
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

        State.Projects.Add(project);
        SetActiveProjectId(project.Id, shifted);
        PersistProject(project, workspace: true);
        DisposeInactiveTemporaryProjects(persistWorkspace: true);
        return project;
    }

    // Where a held capture gesture (Ctrl+C, Ctrl+X, Ctrl+A) files by default: the
    // lane's active project, or the Journal when none is active. Looking this up
    // never changes which project is active; activation is only ever the user's
    // decision (a dialog saved with its Activate toggle on, the Board, Ctrl+J).
    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlProject GetCaptureHome(bool shifted = false)
    {
        var project = ResolveActiveCaptureProject(shifted)
            ?? GetOrCreateJournalProject(shifted, activate: false);
        // A journal always has today's day bucket present before the capture path
        // resolves a target, highlighted unless the user picked a bucket of their
        // own. Its Capture / Quick Note children stay lazy.
        EnsureJournalDayBucket(
            project,
            DateTime.Now,
            setActive: UserSelectedJournalBucket(project) is null);
        return project;
    }

    // Where a tapped Ctrl+C is auto-captured: the lane's active project, else the
    // Journal when the idle-copy setting captures there, else nowhere (null).
    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlProject? GetTapCaptureProject(bool shifted = false)
    {
        return ResolveActiveCaptureProject(shifted)
            ?? (ZetlIdleCopyCapture.CapturesToJournal(Defaults.IdleCopyCapture)
                ? GetOrCreateJournalProject(shifted, activate: false)
                : null);
    }

    // Count a capture into <project> as activity for the auto-return window. Call
    // before the capture's own write so the timestamp persists with it.
    public void RecordCaptureActivity(ZetlProject project) => TouchProjectActivity(project);

    // The lane's active project as capture should see it. A journal from a period
    // that has rolled over hands its activation to the current journal, and a
    // deliberate project quiet past the auto-return window is switched off.
    private ZetlProject? ResolveActiveCaptureProject(bool shifted)
    {
        if (GetActiveProject(shifted) is not { } active)
        {
            return null;
        }

        if (active.JournalMode)
        {
            var expectedName = ExpectedJournalProjectName(DateTime.Now, shifted);
            return IsFormattedJournalName(active.Name, shifted)
                && !string.Equals(active.Name, expectedName, StringComparison.OrdinalIgnoreCase)
                    ? GetOrCreateJournalProject(shifted, activate: true)
                    : active;
        }

        if (!IsQuietPastAutoReturn(active))
        {
            return active;
        }

        // Quiet past the configured window: return the lane to no project, so a
        // forgotten project never traps captures. The idle-copy setting then decides
        // whether tapped copies go to the Journal.
        SetActiveProjectId(null, shifted);
        PersistWorkspace();
        DisposeInactiveTemporaryProjects(persistWorkspace: true);
        return null;
    }

    // True when a deliberate project should be switched off: the auto-return
    // window is on and it has had no capture for at least that long.
    private bool IsQuietPastAutoReturn(ZetlProject active)
    {
        if (active.JournalMode || active.LastActiveUtc == default)
        {
            return false;
        }

        var hours = Defaults.JournalAutoReturnHours;
        return hours > 0 && DateTime.UtcNow - active.LastActiveUtc >= TimeSpan.FromHours(hours);
    }

    // Mark a deliberate project as just used, refreshing its auto-return window. The
    // Journal never auto-returns, so it is left untouched (and pays no extra cost).
    private static void TouchProjectActivity(ZetlProject project)
    {
        if (!project.JournalMode)
        {
            project.LastActiveUtc = DateTime.UtcNow;
        }
    }

    // The lane's default journal, created on first use and tracked by id (so a rename
    // never loses it). Activated only when the caller asks (Ctrl+J, a rolled-over
    // journal that was active); capture lookups use it without activating it.
    private ZetlProject GetOrCreateJournalProject(bool shifted, bool activate)
    {
        var pointerId = shifted ? State.ShiftDefaultJournalProjectId : State.DefaultJournalProjectId;
        var expectedName = ExpectedJournalProjectName(DateTime.Now, shifted);
        // Reuse the journal only while it is still Active and matches the current expected name.
        // If it was finished, archived, or the interval rolled, capture mints a fresh journal.
        if (pointerId is not null
            && State.Projects.FirstOrDefault(project => project.Id == pointerId) is { } existing
            && IsActiveStatus(existing)
            && (!IsFormattedJournalName(existing.Name, shifted) || string.Equals(existing.Name, expectedName, StringComparison.OrdinalIgnoreCase)))
        {
            if (activate)
            {
                SetActiveProjectId(existing.Id, shifted);
                PersistWorkspace();
                DisposeInactiveTemporaryProjects(persistWorkspace: true);
            }

            return existing;
        }

        var journal = new ZetlProject
        {
            Id = NewId(),
            Name = expectedName,
            JournalMode = true
        };
        State.Projects.Add(journal);
        if (shifted)
        {
            State.ShiftDefaultJournalProjectId = journal.Id;
        }
        else
        {
            State.DefaultJournalProjectId = journal.Id;
        }

        // Pre-seed the whole period's day-parent buckets so future/past days are
        // ready as reminder slots, then highlight today.
        SeedJournalDayBuckets(journal);
        EnsureJournalDayBucket(journal, DateTime.Now, setActive: true);
        if (activate)
        {
            SetActiveProjectId(journal.Id, shifted);
        }

        PersistProject(journal, workspace: true);
        DisposeInactiveTemporaryProjects(persistWorkspace: true);
        return journal;
    }

    // Held Ctrl+J toggles the lane between the Journal and the last-used deliberate
    // project: on a deliberate project it returns to the Journal; on the Journal it
    // reactivates the last deliberate project (when one is still Active).
    [MethodImpl(MethodImplOptions.Synchronized)]
    public (ZetlProjectToggleOutcome Outcome, string? ProjectName) ToggleActiveProject(bool shifted = false)
    {
        if (GetActiveProject(shifted) is { JournalMode: false } active)
        {
            // The deactivated project stays recorded as the last deliberate project
            // (set when it was activated), so the next toggle brings it back.
            var journal = GetOrCreateJournalProject(shifted, activate: true);
            return (ZetlProjectToggleOutcome.ReturnedToJournal, journal.Name);
        }

        var lastId = shifted ? State.ShiftLastDeliberateProjectId : State.LastDeliberateProjectId;
        var last = lastId is null
            ? null
            : State.Projects.FirstOrDefault(project =>
                project.Id == lastId && !project.JournalMode && IsActiveStatus(project));
        if (last is null)
        {
            return (ZetlProjectToggleOutcome.NoProjectToActivate, null);
        }

        SetActiveProjectId(last.Id, shifted);
        PersistWorkspace();
        return (ZetlProjectToggleOutcome.Activated, last.Name);
    }

    // Ensure the day-parent bucket for <localNow> exists (named e.g. "Mon 07-06",
    // shifted by the configured day-start hour). Returns it; optionally highlights it
    // as the active bucket. No-op (returns null) for a non-journal project.
    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlBucket? EnsureJournalDayBucket(ZetlProject project, DateTime localNow, bool setActive = false)
    {
        if (!project.JournalMode)
        {
            return null;
        }

        return GetOrCreateChildBucket(project, null, JournalBucketName(localNow, Defaults.DayStartHour), setActive);
    }

    // The copy-capture target for a journal: a bucket the user selected, else
    // today's day parent's "Capture" child, created on first use and made the
    // active bucket. No-op (null) for a non-journal project. Named RollJournalBucket
    // because it also advances the active day.
    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlBucket? RollJournalBucket(ZetlProject project, DateTime localNow)
    {
        if (!project.JournalMode)
        {
            return null;
        }

        if (UserSelectedJournalBucket(project) is { } selected)
        {
            return selected;
        }

        var day = EnsureJournalDayBucket(project, localNow)!;
        return GetOrCreateChildBucket(project, day.Id, JournalCaptureBucketName, setActive: true);
    }

    // The journal's active bucket when the user chose it: anything other than the
    // day buckets Zetl manages (a dated day, or its Capture / Quick Note child),
    // which roll forward with the calendar instead.
    private static ZetlBucket? UserSelectedJournalBucket(ZetlProject project)
    {
        if (!project.JournalMode
            || project.Buckets.FirstOrDefault(bucket => bucket.Id == project.ActiveBucketId) is not { } active
            || IsDeletedBucket(active)
            || IsReservedBucketName(active.Name)
            || IsJournalDayBucket(active))
        {
            return null;
        }

        var isManagedDayChild = active.ParentBucketId is { } parentId
            && project.Buckets.Any(parent => parent.Id == parentId && IsJournalDayBucket(parent))
            && (string.Equals(active.Name, JournalCaptureBucketName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(active.Name, JournalQuickNoteBucketName, StringComparison.OrdinalIgnoreCase));
        return isManagedDayChild ? null : active;
    }

    // A top-level dated day bucket, named like JournalBucketName ("Mon 07-06").
    private static bool IsJournalDayBucket(ZetlBucket bucket) =>
        bucket.ParentBucketId is null
        && System.Text.RegularExpressions.Regex.IsMatch(bucket.Name, @"^[A-Z][a-z]{2} \d{2}-\d{2}$");

    // The quick-note target for a journal: today's day parent's "Quick Note" child,
    // created on first use. Does not steal the active bucket from the copy target.
    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlBucket? ResolveJournalQuickNoteBucket(ZetlProject project, DateTime localNow)
    {
        if (!project.JournalMode)
        {
            return null;
        }

        var day = EnsureJournalDayBucket(project, localNow)!;
        return GetOrCreateChildBucket(project, day.Id, JournalQuickNoteBucketName, setActive: false);
    }

    // Seed the whole period's day-parent buckets up front — Mon–Sun for Weekly, the
    // full month for Monthly, just today for Daily — in chronological order so the
    // canonical bucket order reads as a calendar. Future and past days become empty
    // reminder slots; their Capture / Quick Note children stay lazy.
    private void SeedJournalDayBuckets(ZetlProject project)
    {
        if (!project.JournalMode)
        {
            return;
        }

        var dayStart = Math.Clamp(Defaults.DayStartHour, 0, 23);
        var shiftedToday = DateTime.Now.AddHours(-dayStart).Date;
        var interval = ZetlJournalInterval.Normalize(Defaults.JournalInterval);

        DateTime firstDay;
        int dayCount;
        if (interval == ZetlJournalInterval.Weekly)
        {
            // ISO week starts Monday: DayOfWeek has Sunday = 0, so shift to Monday = 0.
            var offsetFromMonday = ((int)shiftedToday.DayOfWeek + 6) % 7;
            firstDay = shiftedToday.AddDays(-offsetFromMonday);
            dayCount = 7;
        }
        else if (interval == ZetlJournalInterval.Monthly)
        {
            firstDay = new DateTime(shiftedToday.Year, shiftedToday.Month, 1);
            dayCount = DateTime.DaysInMonth(shiftedToday.Year, shiftedToday.Month);
        }
        else
        {
            firstDay = shiftedToday;
            dayCount = 1;
        }

        for (var i = 0; i < dayCount; i++)
        {
            // Re-add the day-start hour so JournalBucketName's own shift lands the name
            // back on this calendar day.
            var probe = firstDay.AddDays(i).AddHours(dayStart);
            GetOrCreateChildBucket(project, null, JournalBucketName(probe, dayStart), setActive: false);
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ConsolidateDefaultProject(bool shifted = false)
    {
        // ConsolidateProjectsNamed persists the merged project and removes the
        // duplicates' files itself, so there is nothing extra to save here.
        ConsolidateProjectsNamed(DefaultProjectName(shifted));
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void UpdateProjectName(ZetlProject project, string name, bool shifted = false)
    {
        project.Name = NormalizeName(name, DefaultProjectName(shifted));
        project.MetadataRevision++;
        PersistProject(project);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetJournalMode(ZetlProject project, bool journalMode)
    {
        project.JournalMode = journalMode;
        project.MetadataRevision++;
        PersistProject(project);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void MarkTemporaryConsumableProject(ZetlProject project, string? sourceTemplateId, bool shifted = false)
    {
        project.Kind = TemporaryConsumableProjectKind;
        project.SourceTemplateId = string.IsNullOrWhiteSpace(sourceTemplateId) ? null : sourceTemplateId.Trim();
        project.TemporaryLane = shifted ? ShiftLane : NormalLane;
        project.MetadataRevision++;
        PersistProject(project, workspace: true);
        DisposeInactiveTemporaryProjects(persistWorkspace: true);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetProjectStatus(ZetlProject project, string status)
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

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetProjectDefaultView(ZetlProject project, string? viewId)
    {
        project.DefaultViewId = string.IsNullOrWhiteSpace(viewId) ? null : viewId.Trim();
        project.MetadataRevision++;
        PersistProject(project);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SaveProjectView(ZetlProject project, ZetlViewDocument view)
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

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void DeleteProjectView(ZetlProject project, string viewId)
    {
        project.Views.RemoveAll(view => string.Equals(view.Id, viewId, StringComparison.Ordinal));
        if (string.Equals(project.DefaultViewId, viewId, StringComparison.Ordinal))
        {
            project.DefaultViewId = null;
        }

        project.MetadataRevision++;
        PersistProject(project);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ClearActiveProject(bool shifted = false)
    {
        SetActiveProjectId(null, shifted);
        PersistWorkspace();
        DisposeInactiveTemporaryProjects(persistWorkspace: true);
    }

    // Seal a project by id: mark it Finished and clear it from whichever lane(s)
    // it occupies, so the next capture advances to a fresh dated session. Used by
    // the compile dialog, whose source project may be any project (not just the
    // current lane's). No-op (returns null) for an unknown id.
    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlProject? FinishProject(string projectId)
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

    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlProject? CreateTemporaryProjectFromReplaySource(
        ZetlProject sourceProject,
        string name,
        string temporaryLane,
        out int replayItemCount)
    {
        replayItemCount = 0;
        var lane = CanonicalTemporaryLane(temporaryLane);
        if (lane is null || !CanCreateTemporaryFromReplay(sourceProject))
        {
            return null;
        }

        var sources = ReplaySourceBuckets(sourceProject)
            .Select(bucket => new
            {
                SourceBucket = bucket,
                Slips = ReplaySourceSlips(sourceProject, bucket).ToList()
            })
            .Where(source => source.Slips.Count > 0)
            .ToList();
        if (sources.Count == 0)
        {
            return null;
        }

        var shifted = string.Equals(lane, ShiftLane, StringComparison.Ordinal);
        var project = CreateProject(
            name,
            sources.Select(source => source.SourceBucket.Name),
            sources[0].SourceBucket.Name,
            shifted,
            kind: TemporaryConsumableProjectKind,
            temporaryLane: lane);
        foreach (var source in sources)
        {
            var targetBucket = project.Buckets.FirstOrDefault(bucket =>
                string.Equals(bucket.Name, source.SourceBucket.Name, StringComparison.OrdinalIgnoreCase));
            if (targetBucket is null)
            {
                continue;
            }

            targetBucket.Settings.Kind = "Replay";
            targetBucket.Settings.DefaultKind = "Replay";
            targetBucket.Settings.DefaultCompileMode = NormalizeCompileMode(
                source.SourceBucket.Settings.DefaultCompileMode);
            targetBucket.Settings.DefaultStartingText =
                (source.SourceBucket.Settings.DefaultStartingText ?? "").Trim();
            targetBucket.Settings.DefaultTsvRowLength =
                source.SourceBucket.Settings.DefaultTsvRowLength <= 0
                    ? 5
                    : source.SourceBucket.Settings.DefaultTsvRowLength;
            targetBucket.Settings.PopMode = false;
            targetBucket.Settings.ReplayReviewBucketId = null;
            targetBucket.Slips.Clear();

            foreach (var note in source.Slips)
            {
                targetBucket.Slips.Add(CloneSlip(note, "temporary-replay", CopyImageAssetAcross(sourceProject, project, note.Image)));
                replayItemCount++;
            }

            targetBucket.Revision++;
        }

        PersistProject(project, workspace: true);
        return project;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void DeleteProject(string projectId)
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

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryDisposeTemporaryReplayProject(ZetlBucket activeBucket, bool shifted, out string projectName)
    {
        projectName = "";
        var project = OwnerProject(activeBucket);
        if (project is null || !IsTemporaryConsumableProject(project))
        {
            return false;
        }

        var lane = shifted ? ShiftLane : NormalLane;
        if (!string.Equals(project.TemporaryLane, lane, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var activeProjectId = shifted ? State.ShiftActiveProjectId : State.ActiveProjectId;
        if (!string.Equals(activeProjectId, project.Id, StringComparison.Ordinal))
        {
            return false;
        }

        projectName = project.Name;
        SetActiveProjectId(null, shifted);
        DisposeInactiveTemporaryProjects(persistWorkspace: true);
        return true;
    }

    // setActive controls whether the new/found bucket becomes the project's
    // active bucket. Compiling into a bucket passes false so it never reshuffles
    // the destination project's active bucket (which may not even be the project
    // you are working in).
    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlBucket AddBucket(ZetlProject project, string name, string? parentBucketId = null, bool setActive = true)
    {
        var normalizedName = NormalizeName(name, "New Bucket");
        if (ResolveReservedBucket(project, normalizedName) is { } reserved)
        {
            return reserved;
        }

        var bucket = CreateBucket(normalizedName);
        ApplyBucketDefaults(bucket);
        bucket.ParentBucketId = project.Buckets.Any(item => item.Id == parentBucketId && !IsDeletedBucket(item))
            ? parentBucketId
            : null;
        project.Buckets.Add(bucket);
        if (setActive)
        {
            project.ActiveBucketId = bucket.Id;
        }

        PersistProject(project);
        return bucket;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlBucket GetOrCreateBucket(ZetlProject project, string name, bool setActive = true)
    {
        var normalizedName = NormalizeName(name, "New Bucket");
        if (ResolveReservedBucket(project, normalizedName) is { } reserved)
        {
            return reserved;
        }

        var bucket = project.Buckets.FirstOrDefault(item =>
            !IsDeletedBucket(item)
            && string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (bucket is not null)
        {
            if (setActive)
            {
                project.ActiveBucketId = bucket.Id;
            }

            PersistProject(project);
            return bucket;
        }

        return AddBucket(project, normalizedName, setActive: setActive);
    }

    // Like GetOrCreateBucket but scoped to a parent: a name matches only among the
    // given parent's own children. This is what lets every journal day carry its own
    // "Capture" / "Quick Note" child without the seven of each colliding by name.
    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlBucket GetOrCreateChildBucket(ZetlProject project, string? parentBucketId, string name, bool setActive = true)
    {
        var normalizedName = NormalizeName(name, "New Bucket");
        if (ResolveReservedBucket(project, normalizedName) is { } reserved)
        {
            return reserved;
        }

        var bucket = project.Buckets.FirstOrDefault(item =>
            !IsDeletedBucket(item)
            && string.Equals(item.ParentBucketId, parentBucketId, StringComparison.Ordinal)
            && string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (bucket is not null)
        {
            if (setActive)
            {
                project.ActiveBucketId = bucket.Id;
            }

            PersistProject(project);
            return bucket;
        }

        return AddBucket(project, normalizedName, parentBucketId, setActive);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetBucketHeading(ZetlBucket bucket, string align, bool bold, int level)
    {
        if (IsDeletedBucket(bucket))
        {
            return;
        }

        var normalized = ZetlViewRenderer.NormalizeHeadingAlign(align);
        bucket.HeadingAlign = normalized == "left" ? "" : normalized;
        bucket.HeadingBold = bold;
        bucket.HeadingLevel = Math.Clamp(level, 0, 6);
        bucket.Revision++;
        PersistBucket(bucket);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool UpdateBucketName(ZetlBucket bucket, string name)
    {
        if (IsScratchBucket(bucket) || IsDeletedBucket(bucket) || IsReservedBucketName(name))
        {
            return false;
        }

        bucket.Name = NormalizeName(name, "Bucket");
        bucket.Revision++;
        PersistBucket(bucket);
        return true;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void DeleteBucket(ZetlProject project, string bucketId)
    {
        var bucket = project.Buckets.FirstOrDefault(item => item.Id == bucketId);
        if (bucket is null || IsScratchBucket(bucket) || IsDeletedBucket(bucket))
        {
            return;
        }

        var idsToRemove = GetBucketAndDescendantIds(project, bucket.Id);
        project.Buckets.RemoveAll(item => idsToRemove.Contains(item.Id));
        EnsureScratchBucket(project.Buckets);
        if (project.ActiveBucketId is null
            || idsToRemove.Contains(project.ActiveBucketId)
            || project.Buckets.All(item => item.Id != project.ActiveBucketId || IsDeletedBucket(item)))
        {
            project.ActiveBucketId = FirstActiveWorkflowBucket(project)?.Id;
        }

        if (project.QuickNoteBucketId is not null
            && (idsToRemove.Contains(project.QuickNoteBucketId)
                || project.Buckets.All(item => item.Id != project.QuickNoteBucketId || IsDeletedBucket(item))))
        {
            project.QuickNoteBucketId = null;
        }

        PersistProject(project);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlSlip AddSlip(
        ZetlBucket bucket,
        string text,
        string source,
        ZetlCaptureOrigin? captureOrigin) =>
        AddSlip(bucket, text, source, null, null, captureOrigin);

    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlSlip AddSlip(
        ZetlBucket bucket,
        string text,
        string source,
        string? noteSessionId = null,
        DateTime? createdAtUtc = null,
        ZetlCaptureOrigin? captureOrigin = null,
        string? title = null,
        string? blockKind = null,
        bool? ignoreBucketRenderKind = null,
        string? richHtml = null,
        IReadOnlyList<ZetlClipboardFormatData>? replayFormats = null)
    {
        var note = new ZetlSlip
        {
            Id = NewId(),
            Title = (title ?? "").Trim(),
            Text = text.Trim(),
            RichHtml = string.IsNullOrWhiteSpace(richHtml) ? null : richHtml,
            ReplayFormats = CloneReplayFormats(replayFormats),
            Source = source,
            SessionId = noteSessionId ?? sessionId,
            CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow,
            CaptureOrigin = captureOrigin,
            BlockKind = ZetlBlockKinds.Normalize(blockKind),
            IgnoreBucketRenderKind = ignoreBucketRenderKind == true
        };
        bucket.Slips.Add(note);
        PersistBucket(bucket);
        return note;
    }

    // With preferTextContent, the caption is the slip's text *content* and the
    // slip presents as Text/Url with the picture attached — a dual capture where
    // the clipboard carried both formats (e.g. spreadsheet cells). Type stays
    // the preferred representation; the picture rides along for Kastn.
    public ZetlSlip AddImageSlip(
        ZetlProject project,
        ZetlBucket bucket,
        ZetlClipboardImage image,
        string source,
        ZetlCaptureOrigin? captureOrigin = null,
        string? caption = null,
        string? sourceUrl = null,
        bool preferTextContent = false,
        string? richHtml = null,
        IReadOnlyList<ZetlClipboardFormatData>? replayFormats = null)
    {
        if (!project.Buckets.Any(item => item.Id == bucket.Id))
        {
            throw new InvalidOperationException("The image destination bucket does not belong to the project.");
        }

        var note = new ZetlSlip
        {
            Id = NewId(),
            Type = ZetlSlipType.Picture,
            Text = (caption ?? "").Trim(),
            RichHtml = string.IsNullOrWhiteSpace(richHtml) ? null : richHtml,
            ReplayFormats = CloneReplayFormats(replayFormats),
            Image = CreateImageAsset(project, image, sourceUrl),
            Source = source,
            SessionId = sessionId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CaptureOrigin = captureOrigin
        };
        if (preferTextContent && !string.IsNullOrWhiteSpace(note.Text))
        {
            note.Type = ZetlSlipClassifier.LooksLikeUrl(note.Text)
                ? ZetlSlipType.Url
                : ZetlSlipType.Text;
        }

        bucket.Slips.Add(note);
        PersistProject(project);
        return note;
    }

    // Attach or replace a slip's picture. The Image setter keeps a slip with
    // text content presenting as text (a dual slip) and turns a text-less slip
    // into a picture slip. The prior asset file stays content-addressed on disk.
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetSlipImage(ZetlProject project, ZetlSlip note, ZetlClipboardImage image)
    {
        note.Image = CreateImageAsset(project, image, sourceUrl: null);
        note.Revision++;
        PersistProject(project);
    }

    // Detach a slip's picture. The Image setter returns a picture-presenting
    // slip to its text (or URL) representation.
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RemoveSlipImage(ZetlProject project, ZetlSlip note)
    {
        note.Image = null;
        note.Revision++;
        PersistProject(project);
    }

    private ZetlImageAsset CreateImageAsset(
        ZetlProject project,
        ZetlClipboardImage image,
        string? sourceUrl)
    {
        if (image.PngBytes.Length == 0 || image.Width <= 0 || image.Height <= 0)
        {
            throw new InvalidDataException("The image is empty or has invalid dimensions.");
        }

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(image.PngBytes))
            .ToLowerInvariant();
        var relativePath = projectStorage.WriteAsset(project, hash, ".png", image.PngBytes);
        return new ZetlImageAsset
        {
            RelativePath = relativePath,
            SourceUrl = sourceUrl,
            Width = image.Width,
            Height = image.Height,
            ByteLength = image.PngBytes.LongLength,
            Sha256 = hash
        };
    }

    public byte[]? ReadImageAsset(ZetlProject project, ZetlSlip note)
    {
        return note.Image is not null
            ? projectStorage.ReadAsset(project, note.Image.RelativePath)
            : null;
    }

    public IReadOnlyList<ZetlProjectAssetFile> GetProjectAssets(ZetlProject project) =>
        projectStorage.GetAssets(project);

    // Adds one note per non-blank text, preserving order, with a single save.
    // Used by a structured compile-to-bucket that keeps notes separate instead
    // of flattening them into one combined note.
    [MethodImpl(MethodImplOptions.Synchronized)]
    public IReadOnlyList<ZetlSlip> AddSlips(ZetlBucket bucket, IEnumerable<string> texts, string source)
    {
        var added = new List<ZetlSlip>();
        foreach (var text in texts)
        {
            var trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var note = new ZetlSlip
            {
                Id = NewId(),
                Text = trimmed,
                Source = source,
                SessionId = sessionId,
                CreatedAtUtc = DateTime.UtcNow
            };
            bucket.Slips.Add(note);
            added.Add(note);
        }

        if (added.Count > 0)
        {
            PersistBucket(bucket);
        }

        return added;
    }

    // Appends activity-log lines as notes in a dedicated "Zetl Logs" project,
    // grouped into a per-day bucket. The log project is infrastructure: it is
    // never made the active project, so it cannot hijack a lane. Retention is
    // bounded (the day bucket is capped and stale day buckets are dropped) so the
    // file cannot grow without limit, and it saves once per call -- the caller
    // batches lines so logging stays off the per-keystroke path.
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void AppendLogSlips(IReadOnlyCollection<string> messages, int maxDayBuckets, int maxNotesPerBucket)
    {
        if (messages.Count == 0)
        {
            return;
        }

        var project = State.Projects.FirstOrDefault(item =>
            string.Equals(item.Name, LogProjectName, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            project = new ZetlProject { Id = NewId(), Name = LogProjectName };
            State.Projects.Add(project);
        }

        var dayName = DateTime.Now.ToString("yyyy-MM-dd");
        var bucket = project.Buckets.FirstOrDefault(item =>
            string.Equals(item.Name, dayName, StringComparison.OrdinalIgnoreCase));
        if (bucket is null)
        {
            bucket = CreateBucket(dayName);
            ApplyBucketDefaults(bucket);
            project.Buckets.Add(bucket);
        }

        foreach (var message in messages)
        {
            var trimmed = (message ?? "").Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            bucket.Slips.Add(new ZetlSlip
            {
                Id = NewId(),
                Text = trimmed,
                Source = "log",
                SessionId = sessionId,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        if (bucket.Slips.Count > maxNotesPerBucket)
        {
            bucket.Slips.RemoveRange(0, bucket.Slips.Count - maxNotesPerBucket);
        }

        // Day-bucket names are yyyy-MM-dd, so ordinal-descending order is newest
        // first. Keep only the most recent day buckets; never drop Scratch.
        foreach (var stale in project.Buckets
            .Where(item => !IsScratchBucket(item))
            .OrderByDescending(item => item.Name, StringComparer.Ordinal)
            .Skip(maxDayBuckets)
            .ToList())
        {
            project.Buckets.Remove(stale);
        }

        PersistProject(project);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void DeleteSlip(ZetlBucket bucket, string noteId)
    {
        var note = bucket.Slips.FirstOrDefault(item => item.Id == noteId);
        if (note is null)
        {
            return;
        }

        bucket.Slips.Remove(note);
        PersistBucket(bucket);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void UpdateSlip(
        ZetlSlip note,
        string text,
        string? title = null,
        bool? excludedFromViews = null,
        string? align = null,
        string? blockKind = null,
        bool? ignoreBucketRenderKind = null,
        bool? @checked = null,
        IReadOnlyList<ZetlInlineStyleRange>? inlineStyles = null,
        bool? bold = null,
        bool? italic = null,
        bool? strike = null,
        ZetlSlipType? type = null,
        string? fontFamily = null,
        int? fontSize = null,
        string? textColor = null)
    {
        var normalizedText = text.Trim();
        if (!string.Equals(note.Text, normalizedText, StringComparison.Ordinal))
        {
            // Once the visible text is edited, the source application's HTML no
            // longer describes it and must not be replayed as stale content.
            note.RichHtml = null;
            note.ReplayFormats = null;
        }
        note.Text = normalizedText;
        // The preferred representation of a dual slip. Picture requires an
        // attached picture (the caller validates); a text preference is
        // re-classified so a bare link presents as Url.
        if (type is { } preferredType)
        {
            note.Type = preferredType == ZetlSlipType.Picture && note.Image is not null
                ? ZetlSlipType.Picture
                : ZetlSlipClassifier.LooksLikeUrl(note.Text)
                    ? ZetlSlipType.Url
                    : ZetlSlipType.Text;
        }

        if (title is not null)
        {
            note.Title = title.Trim();
        }

        if (excludedFromViews is { } excluded)
        {
            note.ExcludedFromViews = excluded;
        }

        if (align is not null)
        {
            // Normalize to keep JSON clean: left is the implicit default (null).
            var normalized = align.Trim().ToLowerInvariant();
            note.Align = normalized is "center" or "right" ? normalized : null;
        }

        if (blockKind is not null)
        {
            // Normalize to a known kind; anything else (including "paragraph"/"none")
            // clears it back to a plain paragraph.
            note.BlockKind = ZetlBlockKinds.Normalize(blockKind);
        }

        if (ignoreBucketRenderKind is { } ignoreBucket)
        {
            note.IgnoreBucketRenderKind = ignoreBucket;
        }

        if (@checked is { } isChecked)
        {
            note.Checked = isChecked;
        }

        // Checked can be rendered by explicit task notes or by a task bucket composed
        // with another slip kind. Clearing happens only when a command explicitly
        // changes the slip kind away from task.
        if (blockKind is not null && note.BlockKind != ZetlBlockKinds.Task)
        {
            note.Checked = false;
        }

        if (bold is { } isBold)
        {
            note.Bold = isBold;
        }

        if (italic is { } isItalic)
        {
            note.Italic = isItalic;
        }

        if (strike is { } isStrike)
        {
            note.Strike = isStrike;
        }

        if (fontFamily is not null)
        {
            note.FontFamily = ZetlSlipTypography.NormalizeFontFamily(fontFamily);
        }

        if (fontSize is { } authoredFontSize)
        {
            note.FontSize = ZetlSlipTypography.NormalizeFontSize(authoredFontSize);
        }

        if (textColor is not null)
        {
            note.TextColor = ZetlSlipTypography.NormalizeTextColor(textColor);
        }

        note.InlineStyles = ZetlInlineStyles.Normalize(
            note.Text,
            inlineStyles ?? note.InlineStyles);

        note.Revision++;
        PersistSlip(note);
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

    public ZetlBucket GetQuickNoteBucket(ZetlProject project)
    {
        var bucket = project.Buckets.FirstOrDefault(bucket =>
            bucket.Id == project.QuickNoteBucketId && !IsDeletedBucket(bucket));
        return bucket ?? GetScratchBucket(project);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetQuickNoteBucket(ZetlProject project, string bucketId)
    {
        if (project.Buckets.Any(bucket => bucket.Id == bucketId && !IsDeletedBucket(bucket)))
        {
            project.QuickNoteBucketId = bucketId;
            PersistProject(project);
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetBucketPopMode(ZetlBucket bucket, bool popMode)
    {
        if (IsDeletedBucket(bucket))
        {
            bucket.Settings.PopMode = false;
            bucket.Revision++;
            PersistBucket(bucket);
            return;
        }

        if (IsReplayBucket(bucket))
        {
            bucket.Settings.PopMode = false;
            bucket.Revision++;
            PersistBucket(bucket);
            return;
        }

        bucket.Settings.PopMode = popMode;
        bucket.Revision++;
        PersistBucket(bucket);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetBucketKind(ZetlBucket bucket, string kind)
    {
        if (IsDeletedBucket(bucket))
        {
            EnsureDeletedBucketShape(bucket);
            bucket.Revision++;
            PersistBucket(bucket);
            return;
        }

        bucket.Settings.Kind = NormalizeBucketKind(kind);
        if (IsReplayBucket(bucket))
        {
            bucket.Settings.PopMode = false;
        }

        bucket.Revision++;
        PersistBucket(bucket);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void UpdateBucketSettings(
        ZetlBucket bucket,
        string name,
        string defaultKind,
        string defaultCompileMode,
        string defaultStartingText,
        int defaultTsvRowLength)
    {
        if (IsDeletedBucket(bucket))
        {
            EnsureDeletedBucketShape(bucket);
        }
        // Scratch keeps its name; everything else about it stays editable.
        else if (!IsScratchBucket(bucket))
        {
            bucket.Name = NormalizeName(name, "Bucket");
            bucket.Settings.DefaultKind = NormalizeBucketKind(defaultKind);
            bucket.Settings.Kind = bucket.Settings.DefaultKind;
            bucket.Settings.DefaultCompileMode = NormalizeCompileMode(defaultCompileMode);
            bucket.Settings.DefaultStartingText = (defaultStartingText ?? "").Trim();
            bucket.Settings.DefaultTsvRowLength = Math.Max(1, defaultTsvRowLength);
            if (IsReplayBucket(bucket))
            {
                bucket.Settings.PopMode = false;
            }
        }
        else
        {
            bucket.Settings.DefaultKind = NormalizeBucketKind(defaultKind);
            bucket.Settings.Kind = bucket.Settings.DefaultKind;
            bucket.Settings.DefaultCompileMode = NormalizeCompileMode(defaultCompileMode);
            bucket.Settings.DefaultStartingText = (defaultStartingText ?? "").Trim();
            bucket.Settings.DefaultTsvRowLength = Math.Max(1, defaultTsvRowLength);
            if (IsReplayBucket(bucket))
            {
                bucket.Settings.PopMode = false;
            }
        }

        bucket.Revision++;
        PersistBucket(bucket);
    }

    // Creates a bucket carrying its complete definition in one durable write, so
    // the new bucket starts at revision 1 with everything the caller asked for.
    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlBucket AddBucket(ZetlProject project, ZetlBucketDefinition definition)
    {
        var normalizedName = NormalizeName(definition.Name, "New Bucket");
        if (ResolveReservedBucket(project, normalizedName) is { } reserved)
        {
            return reserved;
        }

        var bucket = CreateBucket(normalizedName);
        ApplyBucketDefaults(bucket);
        project.Buckets.Add(bucket);
        ApplyBucketDefinition(project, bucket, definition);
        PersistProject(project);
        return bucket;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void UpdateBucket(ZetlProject project, ZetlBucket bucket, ZetlBucketDefinition definition)
    {
        if (!project.Buckets.Any(item => item.Id == bucket.Id))
        {
            return;
        }

        if (IsDeletedBucket(bucket))
        {
            EnsureDeletedBucketShape(bucket);
            bucket.Revision++;
            PersistProject(project);
            return;
        }

        ApplyBucketDefinition(project, bucket, definition);
        bucket.Revision++;
        PersistProject(project);
    }

    // Every field lands before the caller persists: a value assigned after the
    // write reports success without ever reaching disk.
    private static void ApplyBucketDefinition(
        ZetlProject project,
        ZetlBucket bucket,
        ZetlBucketDefinition definition)
    {
        if (!IsScratchBucket(bucket) && !IsReservedBucketName(definition.Name))
        {
            bucket.Name = NormalizeName(definition.Name, "Bucket");
        }

        bucket.ParentBucketId = definition.ParentBucketId != bucket.Id
            && project.Buckets.Any(item => item.Id == definition.ParentBucketId && !IsDeletedBucket(item))
                ? definition.ParentBucketId
                : null;
        bucket.Settings.Kind = NormalizeBucketKind(definition.Kind);
        bucket.Settings.DefaultKind = NormalizeBucketKind(definition.DefaultKind);
        bucket.Settings.DefaultCompileMode = NormalizeCompileMode(definition.DefaultCompileMode);
        bucket.Settings.DefaultStartingText = (definition.DefaultStartingText ?? "").Trim();
        bucket.Settings.DefaultTsvRowLength = Math.Max(1, definition.DefaultTsvRowLength);
        bucket.Settings.PopMode = !IsReplayBucket(bucket) && definition.PopMode;
        bucket.Settings.ReplayReviewBucketId = definition.ReplayReviewBucketId != bucket.Id
            && project.Buckets.Any(item => item.Id == definition.ReplayReviewBucketId && !IsDeletedBucket(item))
                ? definition.ReplayReviewBucketId
                : null;
        if (definition.RenderKind is not null)
        {
            bucket.RenderKind = ZetlBucketRenderKinds.Normalize(definition.RenderKind);
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool MoveSlip(ZetlProject project, ZetlSlip note, ZetlBucket destination)
    {
        var source = project.Buckets.FirstOrDefault(bucket =>
            bucket.Slips.Any(item => item.Id == note.Id));
        if (source is null
            || project.Buckets.All(bucket => bucket.Id != destination.Id)
            || source.Id == destination.Id)
        {
            return false;
        }

        source.Slips.RemoveAll(item => item.Id == note.Id);
        destination.Slips.Add(note);
        if (IsDeletedBucket(destination))
        {
            note.DeletedFromBucketId = source.Id;
            note.DeletedAtUtc = DateTime.UtcNow;
        }
        else if (IsDeletedBucket(source))
        {
            note.DeletedFromBucketId = null;
            note.DeletedAtUtc = null;
        }

        note.Revision++;
        PersistProject(project);
        return true;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool ReorderSlip(ZetlProject project, ZetlSlip note, string? beforeNoteId)
    {
        var bucket = project.Buckets.FirstOrDefault(bucket =>
            bucket.Slips.Any(item => item.Id == note.Id));
        if (bucket is null)
        {
            return false;
        }

        var currentIndex = bucket.Slips.FindIndex(item => item.Id == note.Id);
        int targetIndex;
        if (beforeNoteId is null)
        {
            targetIndex = bucket.Slips.Count;
        }
        else
        {
            var anchorIndex = bucket.Slips.FindIndex(item => item.Id == beforeNoteId);
            if (anchorIndex < 0)
            {
                return false;
            }

            targetIndex = anchorIndex;
        }

        bucket.Slips.RemoveAt(currentIndex);
        if (targetIndex > currentIndex)
        {
            targetIndex--;
        }

        targetIndex = Math.Clamp(targetIndex, 0, bucket.Slips.Count);
        bucket.Slips.Insert(targetIndex, note);
        note.Revision++;
        PersistProject(project);
        return true;
    }

    // Reposition a bucket among its siblings (buckets sharing its parent) in the flat
    // project.Buckets list. Only sibling-relative order matters to the tree and
    // renderer — descendants are linked by ParentBucketId, not list adjacency — so the
    // single entry moves and its children come with it implicitly. A null anchor moves
    // the bucket to the end of its sibling group; otherwise it lands immediately before
    // the anchor, which must be a sibling.
    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool ReorderBucket(ZetlProject project, ZetlBucket bucket, string? beforeBucketId)
    {
        var currentIndex = project.Buckets.FindIndex(item => item.Id == bucket.Id);
        if (currentIndex < 0)
        {
            return false;
        }

        int targetIndex;
        if (beforeBucketId is null)
        {
            var lastSiblingIndex = project.Buckets.FindLastIndex(item =>
                item.Id != bucket.Id && item.ParentBucketId == bucket.ParentBucketId);
            targetIndex = lastSiblingIndex < 0 ? project.Buckets.Count : lastSiblingIndex + 1;
        }
        else
        {
            var anchor = project.Buckets.FirstOrDefault(item => item.Id == beforeBucketId);
            if (anchor is null
                || anchor.Id == bucket.Id
                || anchor.ParentBucketId != bucket.ParentBucketId)
            {
                return false;
            }

            targetIndex = project.Buckets.FindIndex(item => item.Id == beforeBucketId);
        }

        project.Buckets.RemoveAt(currentIndex);
        if (targetIndex > currentIndex)
        {
            targetIndex--;
        }

        targetIndex = Math.Clamp(targetIndex, 0, project.Buckets.Count);
        project.Buckets.Insert(targetIndex, bucket);
        bucket.Revision++;
        PersistProject(project);
        return true;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ToggleActiveBucketPopMode(bool shifted = false)
    {
        var bucket = GetActiveBucket(shifted);
        if (bucket is null || IsReplayBucket(bucket))
        {
            return;
        }

        bucket.Settings.PopMode = !bucket.Settings.PopMode;
        bucket.Revision++;
        PersistBucket(bucket);
    }

    // The bucket a copy capture (auto, held, or image) should land in for <project>.
    // Journals roll to today's Capture child; deliberate projects use their active
    // bucket, falling back to Scratch. Centralizes copy-target routing so the shortcut
    // coordinator does not branch on journal mode itself.
    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlBucket ResolveCaptureBucket(ZetlProject project, bool shifted)
    {
        if (project.JournalMode)
        {
            return RollJournalBucket(project, DateTime.Now)!;
        }

        return GetActiveBucket(shifted) ?? GetScratchBucket(project);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlBucket GetScratchBucket(ZetlProject project)
    {
        // A journal project has no Scratch bucket; its quick-note catch-all is today's
        // "Quick Note" child, so the quick-note fallback routes there. (Copy capture
        // routes to the "Capture" child via RollJournalBucket instead.)
        if (project.JournalMode)
        {
            return ResolveJournalQuickNoteBucket(project, DateTime.Now)!;
        }

        var scratch = EnsuredScratchBucket(project);
        if (project.ActiveBucketId is null || project.Buckets.Any(bucket => bucket.Id == project.ActiveBucketId && IsDeletedBucket(bucket)))
        {
            project.ActiveBucketId = scratch.Id;
        }

        return scratch;
    }

    // Creating a bucket under a reserved name resolves to the bucket Zetl already
    // owns under that name, so no second Scratch or Deleted bucket can appear.
    private ZetlBucket? ResolveReservedBucket(ZetlProject project, string name)
    {
        if (IsDeletedBucketName(name))
        {
            return GetDeletedBucket(project);
        }

        if (!IsScratchBucketName(name))
        {
            return null;
        }

        var existed = project.Buckets.Any(IsScratchBucket);
        var scratch = EnsuredScratchBucket(project);
        if (!existed)
        {
            PersistProject(project);
        }

        return scratch;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlBucket GetDeletedBucket(ZetlProject project)
    {
        var bucket = project.Buckets.FirstOrDefault(IsDeletedBucket)
            ?? project.Buckets.FirstOrDefault(bucket => IsDeletedBucketName(bucket.Name));
        if (bucket is null)
        {
            bucket = CreateBucket(DeletedBucketName);
            project.Buckets.Add(bucket);
        }

        EnsureDeletedBucketShape(bucket);
        if (project.ActiveBucketId == bucket.Id)
        {
            project.ActiveBucketId = FirstActiveWorkflowBucket(project)?.Id;
        }

        if (project.QuickNoteBucketId == bucket.Id)
        {
            project.QuickNoteBucketId = null;
        }

        PersistProject(project);
        return bucket;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryPopLastMatchingActiveSlip(string text, bool shifted = false)
    {
        return TryPopLastMatchingActiveSlip(text, shifted, out _, out _);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryPopLastMatchingActiveSlip(string text, bool shifted, out ZetlBucket? bucket, out ZetlSlip? note)
    {
        return TryPopLastMatchingActiveSlip(
            text,
            shifted,
            out bucket,
            out note,
            out _,
            out _);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryPopLastMatchingActiveSlip(
        string text,
        bool shifted,
        out ZetlBucket? bucket,
        out ZetlSlip? note,
        out ZetlBucket? reviewBucket,
        out ZetlSlip? reviewNote)
    {
        bucket = GetActiveBucket(shifted);
        note = null;
        reviewBucket = null;
        reviewNote = null;
        if (bucket is null || IsReplayBucket(bucket) || !bucket.Settings.PopMode || bucket.Slips.Count == 0)
        {
            return false;
        }

        var last = bucket.Slips.LastOrDefault(
            note => IsCurrentSessionSlip(note) && !IsStructuralSlip(note));
        if (last is null)
        {
            return false;
        }

        if (!string.Equals(last.Text, text.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        return TryArchivePoppedSlip(bucket, last, out note, out reviewBucket, out reviewNote);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryPopLastMatchingActiveImage(
        string sha256,
        bool shifted,
        out ZetlBucket? bucket,
        out ZetlSlip? note)
    {
        return TryPopLastMatchingActiveImage(
            sha256,
            shifted,
            out bucket,
            out note,
            out _,
            out _);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryPopLastMatchingActiveImage(
        string sha256,
        bool shifted,
        out ZetlBucket? bucket,
        out ZetlSlip? note,
        out ZetlBucket? reviewBucket,
        out ZetlSlip? reviewNote)
    {
        bucket = GetActiveBucket(shifted);
        note = null;
        reviewBucket = null;
        reviewNote = null;
        if (bucket is null || IsReplayBucket(bucket) || !bucket.Settings.PopMode)
        {
            return false;
        }

        var last = bucket.Slips.LastOrDefault(
            note => IsCurrentSessionSlip(note) && !IsStructuralSlip(note));
        // Match on the attached picture regardless of preferred representation,
        // so a dual (text + picture) capture pops on its image hash too.
        if (last?.Image is null
            || !string.Equals(last.Image.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryArchivePoppedSlip(bucket, last, out note, out reviewBucket, out reviewNote);
    }

    public bool TryPeekNextReplaySlip(ZetlBucket? bucket, out ZetlSlip? note)
    {
        if (bucket is null || !IsReplayBucket(bucket))
        {
            note = null;
            return false;
        }

        note = bucket.Slips.FirstOrDefault(item =>
            !IsStructuralSlip(item)
            && (item.IsImage || !string.IsNullOrWhiteSpace(item.Text)));
        return note is not null;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryConsumeReplaySlip(ZetlBucket bucket, string noteId)
    {
        return TryConsumeReplaySlip(bucket, noteId, out _);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryConsumeReplaySlip(ZetlBucket bucket, string noteId, out ZetlSlip? consumedNote)
    {
        if (!IsReplayBucket(bucket))
        {
            consumedNote = null;
            return false;
        }

        var note = bucket.Slips.FirstOrDefault(item =>
            item.Id == noteId
            && !IsStructuralSlip(item)
            && (item.IsImage || !string.IsNullOrWhiteSpace(item.Text)));
        if (note is null)
        {
            consumedNote = null;
            return false;
        }

        note.Revision++;
        bucket.Slips.Remove(note);
        consumedNote = note;
        PersistBucket(bucket);
        return true;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryConsumeReplaySlipToReview(ZetlProject project, ZetlBucket bucket, string noteId, out ZetlBucket? reviewBucket)
    {
        return TryConsumeReplaySlipToReview(project, bucket, noteId, out reviewBucket, out _, out _);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryConsumeReplaySlipToReview(
        ZetlProject project,
        ZetlBucket bucket,
        string noteId,
        out ZetlBucket? reviewBucket,
        out ZetlSlip? consumedNote,
        out ZetlSlip? reviewNote)
    {
        reviewBucket = null;
        consumedNote = null;
        reviewNote = null;
        if (!IsReplayBucket(bucket))
        {
            return false;
        }

        var note = bucket.Slips.FirstOrDefault(item =>
            item.Id == noteId
            && !IsStructuralSlip(item)
            && (item.IsImage || !string.IsNullOrWhiteSpace(item.Text)));
        if (note is null)
        {
            return false;
        }

        note.Revision++;
        bucket.Slips.Remove(note);
        consumedNote = note;
        if (note.IsImage || !string.IsNullOrWhiteSpace(note.Text))
        {
            reviewBucket = GetOrCreateReplayReviewBucket(project, bucket);
            reviewNote = CloneSlip(note, "replay", CloneImageAssetReference(note.Image), trimText: true);
            reviewBucket.Slips.Add(reviewNote);
        }

        PersistProject(project);
        return true;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RestoreReplayConsumedSlip(ZetlBucket bucket, ZetlSlip note, ZetlBucket? reviewBucket, string? reviewNoteId)
    {
        bucket.Settings.Kind = "Replay";
        bucket.Settings.PopMode = false;
        bucket.Revision++;
        if (reviewBucket is not null && reviewNoteId is not null)
        {
            reviewBucket.Slips.RemoveAll(item => item.Id == reviewNoteId);
        }

        if (bucket.Slips.All(item => item.Id != note.Id))
        {
            bucket.Slips.Insert(0, note);
        }

        PersistBucket(bucket);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RestorePoppedSlip(ZetlBucket bucket, ZetlSlip note, ZetlBucket? reviewBucket, string? reviewNoteId)
    {
        if (reviewBucket is not null && reviewNoteId is not null)
        {
            reviewBucket.Slips.RemoveAll(item => item.Id == reviewNoteId);
        }

        if (bucket.Slips.All(item => item.Id != note.Id))
        {
            bucket.Slips.Add(note);
        }

        PersistBucket(bucket);
    }

    public string CompilePlainTextFromSlips(ZetlProject project, IEnumerable<SlipDisplayItem> selectedNotes)
    {
        var parts = new List<string> { project.Name.Trim(), "" };
        foreach (var group in selectedNotes.GroupBy(item => item.Bucket))
        {
            var depth = ZetlTreeText.BucketDepth(group.Key, project.Buckets);
            parts.Add(ZetlTreeText.IndentedLine(group.Key.Name.Trim(), depth));
            parts.AddRange(group
                .Where(item => !item.Slip.IsImage)
                .Select(item => ZetlTreeText.IndentedText(item.Slip.Text, depth + 1)));
            parts.Add("");
        }

        return string.Join(Environment.NewLine, parts).TrimEnd();
    }

    public string CompileHtmlFromSlips(ZetlProject project, IEnumerable<SlipDisplayItem> selectedNotes)
    {
        var selected = selectedNotes
            .Where(item => !item.Slip.IsImage)
            .Select(item => ZetlProjectSnapshotMapper.ToSnapshot(item.Bucket, item.Slip))
            .ToList();
        var html = ZetlViewRenderer.RenderHtmlBody(
            ZetlProjectSnapshotMapper.ToSnapshot(project),
            selected,
            new ZetlViewDocument
            {
                Id = "compile-formatted-html",
                Name = "Formatted",
                Kind = ZetlViewKinds.Html
            });
        return PrintableTaskBoxes(html);
    }

    // Clipboard targets such as word processors drop form inputs, so a quick
    // worksheet prints its task boxes as characters instead.
    private static string PrintableTaskBoxes(string html) =>
        html
            .Replace(ZetlMarkdown.TaskCheckboxHtml(isChecked: true), "☑ ", StringComparison.Ordinal)
            .Replace(ZetlMarkdown.TaskCheckboxHtml(isChecked: false), "☐ ", StringComparison.Ordinal);

    public string CompileUnformattedFromSlips(IEnumerable<SlipDisplayItem> selectedNotes)
    {
        return string.Join(
            Environment.NewLine,
            selectedNotes
                .Select(item => item.Slip.Text.Trim())
                .Where(text => text.Length > 0));
    }

    public string CompileTsvFromSlips(ZetlProject project, IEnumerable<SlipDisplayItem> selectedNotes, int rowLength)
    {
        var normalizedRowLength = Math.Max(1, rowLength);
        var parts = new List<string> { project.Name.Trim() };
        foreach (var group in selectedNotes.GroupBy(item => item.Bucket))
        {
            parts.Add(group.Key.Name.Trim());
            var headers = ZetlTsv.HeaderCells(group.Key.Settings.DefaultStartingText);
            if (headers.Count > 0)
            {
                parts.Add(string.Join('\t', headers));
            }

            parts.AddRange(ZetlTsv.Rows(group.Select(item => item.Slip.Text), normalizedRowLength));

            parts.Add("");
        }

        return string.Join(Environment.NewLine, parts).TrimEnd();
    }

    public int GetBucketTsvRowLength(ZetlBucket bucket)
    {
        var headerLength = ZetlTsv.HeaderCells(bucket.Settings.DefaultStartingText).Count;
        return headerLength > 0 ? headerLength : Math.Max(1, bucket.Settings.DefaultTsvRowLength);
    }

    public IReadOnlyList<BucketDisplayItem> GetBucketDisplayItems(
        ZetlProject project,
        bool includeDeleted = false)
    {
        var result = new List<BucketDisplayItem>();
        AddChildren(parentId: null, depth: 0);

        foreach (var bucket in project.Buckets
            .Where(bucket => includeDeleted || !IsDeletedBucket(bucket))
            .OrderBy(bucket => bucket.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (result.All(item => item.Bucket.Id != bucket.Id))
            {
                result.Add(new BucketDisplayItem(bucket, bucket.Name));
            }
        }

        return result;

        void AddChildren(string? parentId, int depth)
        {
            foreach (var child in project.Buckets
                .Where(bucket => bucket.ParentBucketId == parentId
                    && (includeDeleted || !IsDeletedBucket(bucket)))
                .OrderBy(bucket => bucket.Name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(new BucketDisplayItem(child, $"{new string(' ', depth * 2)}{child.Name}"));
                AddChildren(child.Id, depth + 1);
            }
        }
    }

    // Compile operates on the whole project by default. Passing
    // currentSessionOnly narrows it to notes captured this session, which the
    // compile dialog exposes as a "This session only" toggle.
    public IReadOnlyList<SlipDisplayItem> GetSlipDisplayItems(ZetlProject project, IReadOnlyList<ZetlBucket>? bucketScope = null, bool currentSessionOnly = false)
    {
        var scopedBucketIds = bucketScope?.Select(bucket => bucket.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<SlipDisplayItem>();
        foreach (var bucketItem in GetBucketDisplayItems(project)
            .Where(item => scopedBucketIds is null || scopedBucketIds.Contains(item.Bucket.Id)))
        {
            foreach (var note in bucketItem.Bucket.Slips.Where(note => IsCompilableSlip(note, currentSessionOnly)))
            {
                result.Add(new SlipDisplayItem(bucketItem.Bucket, note, $"{bucketItem.Label.Trim()}: {PreviewText(note.Text)}"));
            }
        }

        return result;
    }

    public bool TryGetLastSlipDisplayItem(ZetlProject project, IReadOnlyList<ZetlBucket>? bucketScope, out SlipDisplayItem? slip, bool currentSessionOnly = false)
    {
        slip = GetSlipDisplayItems(project, bucketScope, currentSessionOnly)
            .OrderByDescending(item => item.Slip.CreatedAtUtc)
            .FirstOrDefault();
        return slip is not null;
    }

    public bool HasCompilableSlips(ZetlProject project, bool currentSessionOnly = false)
    {
        return project.Buckets.Any(bucket =>
            !IsDeletedBucket(bucket)
            && bucket.Slips.Any(note => IsCompilableSlip(note, currentSessionOnly)));
    }

    private bool IsCompilableSlip(ZetlSlip note, bool currentSessionOnly)
    {
        return (!currentSessionOnly || IsCurrentSessionSlip(note))
            && !IsStructuralSlip(note)
            && !string.IsNullOrWhiteSpace(note.Text);
    }

    // A structural note (divider, and later group/table/latex) is a Kastn-only rendering
    // element with no authored content, so Zetl's capture, compile, Replay, and Pop flows
    // pass over it.
    public static bool IsStructuralSlip(ZetlSlip note) => ZetlBlockKinds.IsStructural(note.BlockKind);

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
    // recently written one that has text to compile.
    public ZetlProject? GetMostRecentCompilableProject()
    {
        return ProjectsByLatestWrite()
            .Where(item => item.Latest is not null)
            .Select(item => item.Project)
            .FirstOrDefault(project => HasCompilableSlips(project));
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

    private void NormalizeLoadedState()
    {
        State.Version = Math.Max(State.Version, 1);
        State.Projects ??= new List<ZetlProject>();
        foreach (var project in State.Projects)
        {
            NormalizeProject(project);
        }

        NormalizeWorkspacePointers();
        DisposeInactiveTemporaryProjects(persistWorkspace: true);
    }

    private void NormalizeProject(ZetlProject project)
    {
        project.Id = string.IsNullOrWhiteSpace(project.Id) ? NewId() : project.Id;
        project.Name = NormalizeName(project.Name, DefaultProjectName());
        project.MetadataRevision = Math.Max(project.MetadataRevision, 1);
        project.ChangeSequence = Math.Max(project.ChangeSequence, 0);
        project.Status = NormalizeProjectStatus(project.Status);
        project.Kind = NormalizeProjectKind(project.Kind);
        project.SourceTemplateId = string.IsNullOrWhiteSpace(project.SourceTemplateId)
            ? null
            : project.SourceTemplateId.Trim();
        project.TemporaryLane = CanonicalTemporaryLane(project.TemporaryLane);
        if (!IsTemporaryConsumableProject(project))
        {
            project.SourceTemplateId = null;
            project.TemporaryLane = null;
        }
        project.Views ??= [];
        foreach (var view in project.Views)
        {
            view.Sections ??= [];
            foreach (var section in view.Sections)
            {
                section.Buckets ??= [];
            }
        }
        project.Buckets ??= new List<ZetlBucket>();
        if (project.JournalMode)
        {
            // A journal has no Scratch bucket — its per-day Quick Note child is the
            // quick-note catch-all. Normalize is the single persist chokepoint, so
            // dropping a stray empty Scratch here also cleans up journals seeded by an
            // earlier build or by a shared bucket-cleanup path.
            RemoveEmptyScratchBucket(project);
        }
        else
        {
            EnsureScratchBucket(project.Buckets);
        }
        foreach (var bucket in project.Buckets)
        {
            bucket.Id = string.IsNullOrWhiteSpace(bucket.Id) ? NewId() : bucket.Id;
            bucket.Revision = Math.Max(bucket.Revision, 1);
            bucket.Name = NormalizeName(bucket.Name, "Bucket");
            if (bucket.ParentBucketId == bucket.Id
                || project.Buckets.All(candidate => candidate.Id != bucket.ParentBucketId || IsDeletedBucket(candidate)))
            {
                bucket.ParentBucketId = null;
            }

            if (bucket.Settings.ReplayReviewBucketId == bucket.Id
                || project.Buckets.All(candidate => candidate.Id != bucket.Settings.ReplayReviewBucketId || IsDeletedBucket(candidate)))
            {
                bucket.Settings.ReplayReviewBucketId = null;
            }

            if (bucket.Settings.PopReviewBucketId == bucket.Id
                || project.Buckets.All(candidate => candidate.Id != bucket.Settings.PopReviewBucketId || IsDeletedBucket(candidate)))
            {
                bucket.Settings.PopReviewBucketId = null;
            }

            bucket.Settings.Kind = NormalizeBucketKind(bucket.Settings.Kind);
            bucket.Settings.DefaultKind = NormalizeBucketKind(string.IsNullOrWhiteSpace(bucket.Settings.DefaultKind) ? bucket.Settings.Kind : bucket.Settings.DefaultKind);
            bucket.Settings.DefaultCompileMode = NormalizeCompileMode(bucket.Settings.DefaultCompileMode);
            bucket.Settings.DefaultStartingText ??= "";
            bucket.Settings.DefaultTsvRowLength = bucket.Settings.DefaultTsvRowLength <= 0 ? 5 : bucket.Settings.DefaultTsvRowLength;
            var headingAlign = ZetlViewRenderer.NormalizeHeadingAlign(bucket.HeadingAlign);
            bucket.HeadingAlign = headingAlign == "left" ? "" : headingAlign;
            bucket.HeadingLevel = Math.Clamp(bucket.HeadingLevel, 0, 6);
            if (IsDeletedBucket(bucket) || IsDeletedBucketName(bucket.Name))
            {
                EnsureDeletedBucketShape(bucket);
            }

            if (IsReplayBucket(bucket))
            {
                bucket.Settings.PopMode = false;
            }
            bucket.Slips ??= new List<ZetlSlip>();
            foreach (var note in bucket.Slips)
            {
                note.Id = string.IsNullOrWhiteSpace(note.Id) ? NewId() : note.Id;
                note.Revision = Math.Max(note.Revision, 1);
                note.Title ??= "";
                note.Text ??= "";
                // Heal an image slip that lost its type (legacy files), but keep
                // dual captures: a slip with text content and a picture is
                // legitimately Text-preferred.
                if (note.Type == ZetlSlipType.Text
                    && note.Image is not null
                    && string.IsNullOrWhiteSpace(note.Text))
                {
                    note.Type = ZetlSlipType.Picture;
                }
                else if (note.Type == ZetlSlipType.Text && ZetlSlipClassifier.LooksLikeUrl(note.Text))
                {
                    note.Type = ZetlSlipType.Url;
                }
                note.Source ??= "";
                note.FontFamily = ZetlSlipTypography.NormalizeFontFamily(note.FontFamily);
                note.FontSize = ZetlSlipTypography.NormalizeFontSize(note.FontSize);
                note.TextColor = ZetlSlipTypography.NormalizeTextColor(note.TextColor);
                if (note.CreatedAtUtc == default)
                {
                    note.CreatedAtUtc = DateTimeOffset.UtcNow;
                }
            }
        }

        if (project.Buckets.All(bucket => bucket.Id != project.ActiveBucketId || IsDeletedBucket(bucket)))
        {
            project.ActiveBucketId = FirstActiveWorkflowBucket(project)?.Id;
        }

        if (project.QuickNoteBucketId is not null
            && project.Buckets.All(bucket => bucket.Id != project.QuickNoteBucketId || IsDeletedBucket(bucket)))
        {
            project.QuickNoteBucketId = null;
        }
    }

    private void NormalizeWorkspacePointers()
    {
        State.Version = Math.Max(State.Version, 1);
        if (State.ActiveProjectId is not null
            && State.Projects.All(project => project.Id != State.ActiveProjectId || !IsActiveStatus(project)))
        {
            State.ActiveProjectId = null;
        }

        if (State.ShiftActiveProjectId is not null
            && State.Projects.All(project => project.Id != State.ShiftActiveProjectId || !IsActiveStatus(project)))
        {
            State.ShiftActiveProjectId = null;
        }

        NormalizeJournalPointer(shifted: false);
        NormalizeJournalPointer(shifted: true);
        NormalizeLastDeliberatePointer(shifted: false);
        NormalizeLastDeliberatePointer(shifted: true);
    }

    private void NormalizeJournalPointer(bool shifted)
    {
        var pointer = shifted ? State.ShiftDefaultJournalProjectId : State.DefaultJournalProjectId;
        var valid = pointer is not null
            && State.Projects.Any(project =>
                project.Id == pointer && project.JournalMode && IsActiveStatus(project));
        if (!valid)
        {
            pointer = null;
        }

        if (pointer is null)
        {
            var activeId = shifted ? State.ShiftActiveProjectId : State.ActiveProjectId;
            pointer = State.Projects.FirstOrDefault(project =>
                    project.Id == activeId && project.JournalMode && IsActiveStatus(project))?.Id
                ?? State.Projects
                    .Where(project => project.JournalMode
                        && IsActiveStatus(project)
                        && IsFormattedJournalName(project.Name, shifted))
                    .OrderByDescending(project => project.ChangeSequence)
                    .Select(project => project.Id)
                    .FirstOrDefault();
        }

        if (shifted)
        {
            State.ShiftDefaultJournalProjectId = pointer;
        }
        else
        {
            State.DefaultJournalProjectId = pointer;
        }
    }

    private void NormalizeLastDeliberatePointer(bool shifted)
    {
        var pointer = shifted ? State.ShiftLastDeliberateProjectId : State.LastDeliberateProjectId;
        var valid = pointer is not null
            && State.Projects.Any(project =>
                project.Id == pointer && !project.JournalMode && IsActiveStatus(project));
        if (!valid)
        {
            pointer = null;
        }

        if (pointer is null)
        {
            var activeId = shifted ? State.ShiftActiveProjectId : State.ActiveProjectId;
            pointer = State.Projects.FirstOrDefault(project =>
                project.Id == activeId && !project.JournalMode && IsActiveStatus(project))?.Id;
        }

        if (shifted)
        {
            State.ShiftLastDeliberateProjectId = pointer;
        }
        else
        {
            State.LastDeliberateProjectId = pointer;
        }
    }

    private static ZetlBucket CreateBucket(string name)
    {
        return new ZetlBucket
        {
            Id = NewId(),
            Name = NormalizeName(name, "Bucket"),
            Settings = new ZetlBucketSettings
            {
                Kind = "Standard",
                DefaultKind = "Standard",
                DefaultCompileMode = "Formatted",
                DefaultTsvRowLength = 5
            }
        };
    }

    private static ZetlBucket? FirstActiveWorkflowBucket(ZetlProject project)
    {
        // Journals have no Scratch; their first day parent is the first workflow bucket.
        if (!project.JournalMode)
        {
            EnsureScratchBucket(project.Buckets);
        }

        return project.Buckets.FirstOrDefault(bucket => !IsDeletedBucket(bucket));
    }

    // Drop a journal's stray empty Scratch bucket. Only removes it when it holds no
    // notes and no child buckets, so a Scratch that somehow gained content is never
    // silently destroyed. Clears any active/quick-note pointer that named it.
    private static void RemoveEmptyScratchBucket(ZetlProject project)
    {
        var scratch = project.Buckets.FirstOrDefault(bucket =>
            !IsDeletedBucket(bucket) && IsScratchBucket(bucket));
        if (scratch is null
            || scratch.Slips.Count > 0
            || project.Buckets.Any(bucket => string.Equals(bucket.ParentBucketId, scratch.Id, StringComparison.Ordinal)))
        {
            return;
        }

        project.Buckets.Remove(scratch);
        if (project.ActiveBucketId == scratch.Id)
        {
            project.ActiveBucketId = null;
        }

        if (project.QuickNoteBucketId == scratch.Id)
        {
            project.QuickNoteBucketId = null;
        }
    }

    private static void EnsureDeletedBucketShape(ZetlBucket bucket)
    {
        bucket.Name = DeletedBucketName;
        bucket.ParentBucketId = null;
        bucket.Settings.Kind = DeletedBucketKind;
        bucket.Settings.DefaultKind = DeletedBucketKind;
        bucket.Settings.PopMode = false;
        bucket.Settings.ReplayReviewBucketId = null;
        bucket.Settings.PopReviewBucketId = null;
    }

    // Stamp a freshly created bucket with the user's default compile mode and
    // TSV row length.
    private void ApplyBucketDefaults(ZetlBucket bucket)
    {
        bucket.Settings.DefaultCompileMode = NormalizeCompileMode(Defaults.CompileMode);
        bucket.Settings.DefaultTsvRowLength = Math.Max(1, Defaults.TsvRowLength);
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
                        DefaultTsvRowLength = sourceBucket.Settings.DefaultTsvRowLength <= 0 ? 5 : sourceBucket.Settings.DefaultTsvRowLength,
                        PopMode = !IsReplayKindValue(sourceKind) && sourceBucket.Settings.PopMode
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
                    targetBucket.Settings.PopMode = false;
                }
                else if (!IsReplayBucket(targetBucket))
                {
                    targetBucket.Settings.PopMode |= sourceBucket.Settings.PopMode;
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

            if (sourceBucket.Settings.PopReviewBucketId is not null
                && bucketMap.TryGetValue(sourceBucket.Id, out targetBucket)
                && bucketMap.TryGetValue(sourceBucket.Settings.PopReviewBucketId, out targetReviewBucket)
                && targetBucket.Id != targetReviewBucket.Id)
            {
                targetBucket.Settings.PopReviewBucketId = targetReviewBucket.Id;
            }
        }

        if (targetProject.QuickNoteBucketId is null
            && sourceProject.QuickNoteBucketId is not null
            && bucketMap.TryGetValue(sourceProject.QuickNoteBucketId, out var quickNoteBucket))
        {
            targetProject.QuickNoteBucketId = quickNoteBucket.Id;
        }
    }

    private static void EnsureBuckets(ZetlProject project, IEnumerable<string> bucketNames)
    {
        foreach (var name in NormalizeBucketNames(bucketNames))
        {
            if (project.Buckets.Any(bucket => string.Equals(bucket.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            project.Buckets.Add(CreateBucket(name));
        }

        EnsureScratchBucket(project.Buckets);
        if (project.Buckets.All(bucket => bucket.Id != project.ActiveBucketId || IsDeletedBucket(bucket)))
        {
            project.ActiveBucketId = FirstActiveWorkflowBucket(project)?.Id;
        }

        if (project.QuickNoteBucketId is not null
            && project.Buckets.All(bucket => bucket.Id != project.QuickNoteBucketId || IsDeletedBucket(bucket)))
        {
            project.QuickNoteBucketId = null;
        }
    }

    private static List<ZetlBucket> OrderParentsFirst(ZetlProject project)
    {
        var ordered = new List<ZetlBucket>(project.Buckets.Count);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var remaining = project.Buckets.ToList();
        var progressed = true;
        while (remaining.Count > 0 && progressed)
        {
            progressed = false;
            for (var i = remaining.Count - 1; i >= 0; i--)
            {
                var bucket = remaining[i];
                var parentReady = bucket.ParentBucketId is null
                    || emitted.Contains(bucket.ParentBucketId)
                    || remaining.All(candidate => candidate.Id != bucket.ParentBucketId);
                if (!parentReady)
                {
                    continue;
                }

                ordered.Add(bucket);
                emitted.Add(bucket.Id);
                remaining.RemoveAt(i);
                progressed = true;
            }
        }

        // Any buckets left reference each other in a parent cycle; append them
        // so consolidation never silently drops a bucket.
        ordered.AddRange(remaining);
        return ordered;
    }

    private static HashSet<string> GetBucketAndDescendantIds(ZetlProject project, string bucketId)
    {
        var ids = new HashSet<string> { bucketId };
        var added = true;
        while (added)
        {
            added = false;
            foreach (var bucket in project.Buckets)
            {
                if (bucket.ParentBucketId is not null && ids.Contains(bucket.ParentBucketId) && ids.Add(bucket.Id))
                {
                    added = true;
                }
            }
        }

        return ids;
    }

    private static void EnsureScratchBucket(List<ZetlBucket> buckets)
    {
        if (buckets.Any(IsScratchBucket))
        {
            return;
        }

        buckets.Add(CreateBucket(ScratchBucketName));
    }

    private static ZetlBucket EnsuredScratchBucket(ZetlProject project)
    {
        EnsureScratchBucket(project.Buckets);
        return project.Buckets.First(IsScratchBucket);
    }

    private static IEnumerable<string> NormalizeBucketNames(IEnumerable<string> bucketNames)
    {
        var names = bucketNames
            .Select(name => NormalizeName(name, ""))
            .Where(name => name.Length > 0)
            .Where(name => !IsDeletedBucketName(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count == 0 ? [JournalCaptureBucketName] : names;
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
            if (!activated.JournalMode && !IsTemporaryConsumableProject(activated))
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

    public static bool IsReplayBucket(ZetlBucket bucket)
    {
        return IsReplayKindValue(bucket.Settings.Kind);
    }

    public static bool CanCreateTemporaryFromReplay(ZetlProject project)
    {
        return ReplaySourceBuckets(project).Any(bucket => ReplaySourceSlips(project, bucket).Any());
    }

    private static IEnumerable<ZetlBucket> ReplaySourceBuckets(ZetlProject project)
    {
        return project.Buckets.Where(bucket =>
            !IsDeletedBucket(bucket)
            && !IsScratchBucket(bucket)
            && (IsReplayBucket(bucket) || ReplayReviewBucket(project, bucket) is not null));
    }

    private static IEnumerable<ZetlSlip> ReplaySourceSlips(ZetlProject project, ZetlBucket bucket)
    {
        if (ReplayReviewBucket(project, bucket) is { } reviewBucket)
        {
            foreach (var note in ReplayableSourceSlips(reviewBucket))
            {
                yield return note;
            }
        }

        foreach (var note in ReplayableSourceSlips(bucket))
        {
            yield return note;
        }
    }

    private static ZetlBucket? ReplayReviewBucket(ZetlProject project, ZetlBucket bucket)
    {
        return bucket.Settings.ReplayReviewBucketId is null
            ? null
            : project.Buckets.FirstOrDefault(candidate =>
                candidate.Id == bucket.Settings.ReplayReviewBucketId
                && candidate.Id != bucket.Id
                && !IsDeletedBucket(candidate));
    }

    private static IEnumerable<ZetlSlip> ReplayableSourceSlips(ZetlBucket bucket)
    {
        return bucket.Slips.Where(note =>
            !IsStructuralSlip(note)
            && (note.IsImage || !string.IsNullOrWhiteSpace(note.Text)));
    }

    // A fresh copy of a slip's authored content and presentation under a new id,
    // stamped as captured now in this session. The caller supplies the picture: a
    // reference to the same asset within a project, or an asset copied across.
    private ZetlSlip CloneSlip(
        ZetlSlip source,
        string cloneSource,
        ZetlImageAsset? image,
        bool trimText = false)
    {
        var clone = new ZetlSlip
        {
            Id = NewId(),
            Type = source.Type,
            Title = source.Title,
            Text = trimText ? source.Text.Trim() : source.Text,
            RichHtml = source.RichHtml,
            ReplayFormats = CloneReplayFormats(source.ReplayFormats),
            Source = cloneSource,
            SessionId = sessionId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExcludedFromViews = source.ExcludedFromViews,
            Align = source.Align,
            BlockKind = source.BlockKind,
            IgnoreBucketRenderKind = source.IgnoreBucketRenderKind,
            Checked = source.Checked,
            Bold = source.Bold,
            Italic = source.Italic,
            Strike = source.Strike,
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            TextColor = source.TextColor,
            InlineStyles = source.InlineStyles.Select(style => style with { }).ToList(),
            CaptureOrigin = source.CaptureOrigin is { } origin
                ? new ZetlCaptureOrigin
                {
                    ApplicationName = origin.ApplicationName,
                    ProcessName = origin.ProcessName,
                    WindowTitle = origin.WindowTitle
                }
                : null
        };

        // Assigned after Text so the Image setter sees the content and keeps a
        // text-preferred dual slip presenting as text.
        clone.Image = image;
        return clone;
    }

    private static List<ZetlClipboardFormatData>? CloneReplayFormats(
        IReadOnlyList<ZetlClipboardFormatData>? formats) =>
        formats is not { Count: > 0 }
            ? null
            : formats.Select(item => new ZetlClipboardFormatData(
                item.Format,
                item.Data.ToArray(),
                item.RegisteredName)).ToList();

    private static ZetlImageAsset? CloneImageAssetReference(ZetlImageAsset? source)
    {
        return source is null
            ? null
            : new ZetlImageAsset
            {
                RelativePath = source.RelativePath,
                SourceUrl = source.SourceUrl,
                MimeType = source.MimeType,
                Width = source.Width,
                Height = source.Height,
                ByteLength = source.ByteLength,
                Sha256 = source.Sha256
            };
    }

    // The picture copied into another project's asset folder, or null when the
    // source has none or its asset file is missing.
    private ZetlImageAsset? CopyImageAssetAcross(
        ZetlProject sourceProject,
        ZetlProject targetProject,
        ZetlImageAsset? source)
    {
        var bytes = source is null ? null : projectStorage.ReadAsset(sourceProject, source.RelativePath);
        if (bytes is null)
        {
            return null;
        }

        var extension = Path.GetExtension(source!.RelativePath);
        var copy = CloneImageAssetReference(source)!;
        copy.RelativePath = projectStorage.WriteAsset(
            targetProject,
            source.Sha256,
            string.IsNullOrWhiteSpace(extension) ? ".png" : extension,
            bytes);
        return copy;
    }

    // The Scratch bucket is special (always present, the quick-note default)
    // and currently cannot be renamed or deleted.
    public static bool IsScratchBucket(ZetlBucket bucket)
    {
        return IsScratchBucketName(bucket.Name);
    }

    public static bool IsDeletedBucket(ZetlBucket bucket)
    {
        return string.Equals(bucket.Settings.Kind, DeletedBucketKind, StringComparison.OrdinalIgnoreCase);
    }

    // Names Zetl owns. No ordinary bucket may take one: Scratch is recognized by
    // name, and the normalizer reshapes any bucket named Deleted into the
    // Deleted bucket, which would silently hide its slips.
    public static bool IsReservedBucketName(string? name)
    {
        return IsScratchBucketName(name) || IsDeletedBucketName(name);
    }

    public static bool IsScratchBucketName(string? name)
    {
        return string.Equals(name?.Trim(), ScratchBucketName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeletedBucketName(string? name)
    {
        return string.Equals(name?.Trim(), DeletedBucketName, StringComparison.OrdinalIgnoreCase);
    }

    // Whether a raw kind string represents Replay Mode (accepts the legacy
    // "Fifo" value as well).
    public static bool IsReplayKind(string? kind)
    {
        return IsReplayKindValue(kind);
    }

    private static bool IsReplayKindValue(string? kind)
    {
        // "Replay" is the stored value; "Fifo" is the legacy value from older
        // state files, mapped forward to "Replay" by NormalizeBucketKind.
        return string.Equals(kind, "Replay", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "Fifo", StringComparison.OrdinalIgnoreCase);
    }

    // The canonical lifecycle status for a raw string, or null when it is not a
    // recognized status. Callers that must reject bad input (the IPC service) use
    // the null result; load-time normalization falls back to Active.
    public static string? CanonicalProjectStatus(string? status)
    {
        if (string.Equals(status, FinishedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return FinishedStatus;
        }

        if (string.Equals(status, ArchivedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return ArchivedStatus;
        }

        if (string.Equals(status, ActiveStatus, StringComparison.OrdinalIgnoreCase))
        {
            return ActiveStatus;
        }

        return null;
    }

    private static string NormalizeProjectStatus(string? status)
    {
        return CanonicalProjectStatus(status) ?? ActiveStatus;
    }

    public static bool IsActiveStatus(ZetlProject project)
    {
        return string.Equals(project.Status, ActiveStatus, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTemporaryConsumableProject(ZetlProject project)
    {
        return string.Equals(project.Kind, TemporaryConsumableProjectKind, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeProjectKind(string? kind)
    {
        return string.Equals(kind, TemporaryConsumableProjectKind, StringComparison.OrdinalIgnoreCase)
            ? TemporaryConsumableProjectKind
            : StandardProjectKind;
    }

    public static string? CanonicalTemporaryLane(string? lane)
    {
        if (string.Equals(lane, ShiftLane, StringComparison.OrdinalIgnoreCase))
        {
            return ShiftLane;
        }

        if (string.Equals(lane, NormalLane, StringComparison.OrdinalIgnoreCase))
        {
            return NormalLane;
        }

        return null;
    }

    private static string NormalizeBucketKind(string? kind)
    {
        if (string.Equals(kind, DeletedBucketKind, StringComparison.OrdinalIgnoreCase))
        {
            return DeletedBucketKind;
        }

        return IsReplayKindValue(kind) ? "Replay" : "Standard";
    }

    private static string NormalizeCompileMode(string? mode)
    {
        if (string.Equals(mode, "Plain", StringComparison.OrdinalIgnoreCase))
        {
            return "Plain";
        }

        if (string.Equals(mode, "TSV", StringComparison.OrdinalIgnoreCase))
        {
            return "TSV";
        }

        return "Formatted";
    }

    private ZetlBucket GetOrCreateReplayReviewBucket(ZetlProject project, ZetlBucket replayBucket)
    {
        var reviewBucket = GetOrCreateReviewBucket(
            project,
            replayBucket,
            replayBucket.Settings.ReplayReviewBucketId,
            $"{replayBucket.Name} Review",
            "Replay Review");
        replayBucket.Settings.ReplayReviewBucketId = reviewBucket.Id;
        replayBucket.Revision++;
        return reviewBucket;
    }

    private bool TryArchivePoppedSlip(
        ZetlBucket sourceBucket,
        ZetlSlip sourceSlip,
        out ZetlSlip? poppedSlip,
        out ZetlBucket? reviewBucket,
        out ZetlSlip? reviewSlip)
    {
        var project = OwnerProject(sourceBucket);
        if (project is null)
        {
            poppedSlip = null;
            reviewBucket = null;
            reviewSlip = null;
            return false;
        }

        reviewBucket = GetOrCreatePopReviewBucket(project, sourceBucket);
        sourceSlip.Revision++;
        sourceBucket.Slips.Remove(sourceSlip);
        poppedSlip = sourceSlip;
        reviewSlip = CloneSlip(sourceSlip, "pop-recovery", CloneImageAssetReference(sourceSlip.Image));
        reviewBucket.Slips.Add(reviewSlip);
        PersistProject(project);
        return true;
    }

    private ZetlBucket GetOrCreatePopReviewBucket(ZetlProject project, ZetlBucket sourceBucket)
    {
        var reviewBucket = GetOrCreateReviewBucket(
            project,
            sourceBucket,
            sourceBucket.Settings.PopReviewBucketId,
            $"{sourceBucket.Name} Pop Review",
            "Pop Review");
        sourceBucket.Settings.PopReviewBucketId = reviewBucket.Id;
        sourceBucket.Revision++;
        return reviewBucket;
    }

    private static ZetlBucket GetOrCreateReviewBucket(
        ZetlProject project,
        ZetlBucket sourceBucket,
        string? linkedReviewBucketId,
        string preferredName,
        string fallbackName)
    {
        var reviewBucket = linkedReviewBucketId is null
            ? null
            : project.Buckets.FirstOrDefault(bucket =>
                bucket.Id == linkedReviewBucketId
                && bucket.Id != sourceBucket.Id
                && !IsDeletedBucket(bucket));
        var normalizedName = NormalizeName(preferredName, fallbackName);
        reviewBucket ??= project.Buckets.FirstOrDefault(bucket =>
            bucket.Id != sourceBucket.Id
            && !IsDeletedBucket(bucket)
            && string.Equals(bucket.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (reviewBucket is null)
        {
            reviewBucket = CreateBucket(normalizedName);
            project.Buckets.Add(reviewBucket);
        }

        reviewBucket.Settings.Kind = "Standard";
        reviewBucket.Settings.PopMode = false;
        return reviewBucket;
    }

    public static string PreviewText(string text)
    {
        var preview = text.ReplaceLineEndings(" ").Trim();
        return preview.Length <= 80 ? preview : $"{preview[..77]}...";
    }

    private static string NewId()
    {
        return Guid.NewGuid().ToString("N");
    }

    public string ExpectedJournalProjectName(DateTime localNow, bool shifted)
    {
        var interval = ZetlJournalInterval.Normalize(Defaults.JournalInterval);
        var baseName = shifted ? "Journal Shift" : "Journal";
        var shiftedNow = localNow.AddHours(-Math.Clamp(Defaults.DayStartHour, 0, 23));

        if (interval == ZetlJournalInterval.Weekly)
        {
            var week = System.Globalization.ISOWeek.GetWeekOfYear(shiftedNow);
            var year = System.Globalization.ISOWeek.GetYear(shiftedNow);
            return $"{baseName} Week {week:D2} {year}";
        }
        else if (interval == ZetlJournalInterval.Monthly)
        {
            return $"{baseName} {shiftedNow:yyyy-MM}";
        }
        else
        {
            return $"{baseName} {shiftedNow:yyyy-MM-dd}";
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public string DefaultProjectName(bool shifted = false)
    {
        return ExpectedJournalProjectName(DateTime.Now, shifted);
    }

    public bool IsFormattedJournalName(string name, bool shifted)
    {
        var prefix = shifted ? "Journal Shift" : "Journal";
        if (string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var suffix = name.Substring(prefix.Length).Trim();
        if (suffix.StartsWith("Week ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = suffix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && int.TryParse(parts[1], out _) && int.TryParse(parts[2], out _))
            {
                return true;
            }
        }
        else if (suffix.Length == 10 && suffix[4] == '-' && suffix[7] == '-')
        {
            var parts = suffix.Split('-');
            if (parts.Length == 3 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _) && int.TryParse(parts[2], out _))
            {
                return true;
            }
        }
        else if (suffix.Length == 7 && suffix[4] == '-')
        {
            var parts = suffix.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _))
            {
                return true;
            }
        }
        return false;
    }

    // The journal "day" a capture belongs to: clock time shifted back by the
    // configured day-start hour, so e.g. with dayStartHour=4 a 1am capture lands in
    // the previous calendar day's bucket. Returns the day-parent bucket name
    // (e.g. "Mon 07-06") — weekday label for at-a-glance reading plus month-day for
    // archival clarity. Invariant culture keeps the weekday stable across locales.
    public static string JournalBucketName(DateTime localNow, int dayStartHour) =>
        localNow.AddHours(-Math.Clamp(dayStartHour, 0, 23))
            .ToString("ddd MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
