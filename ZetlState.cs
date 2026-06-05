namespace ZETL;

internal sealed class ZetlState
{
    public int Version { get; set; } = 1;
    public string? ActiveProjectId { get; set; }
    public string? ShiftActiveProjectId { get; set; }
    public List<ZetlProject> Projects { get; set; } = new();
}

internal sealed class ZetlProject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ActiveBucketId { get; set; }
    public string? QuickNoteBucketId { get; set; }
    public List<ZetlBucket> Buckets { get; set; } = new();
}

internal sealed class ZetlBucket
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ParentBucketId { get; set; }
    public string Kind { get; set; } = "Standard";
    public string DefaultKind { get; set; } = "Standard";
    public string DefaultCompileMode { get; set; } = "Formatted";
    public string DefaultStartingText { get; set; } = "";
    public int DefaultTsvRowLength { get; set; } = 5;
    public bool PopMode { get; set; }
    public string? FifoReviewBucketId { get; set; }
    public List<ZetlNote> Notes { get; set; } = new();
}

internal sealed record BucketDisplayItem(ZetlBucket Bucket, string Label)
{
    public override string ToString()
    {
        return Label;
    }
}

internal sealed record NoteDisplayItem(ZetlBucket Bucket, ZetlNote Note, string Label)
{
    public override string ToString()
    {
        return Label;
    }
}

internal sealed class ZetlNote
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Source { get; set; } = "";
    public string? SessionId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

internal sealed record ZetlBucketDefaults(IReadOnlyList<string> ProjectBuckets, string CompileMode, int TsvRowLength)
{
    public static ZetlBucketDefaults Standard { get; } = new(new[] { "Inbox", "Scratch" }, "Formatted", 5);
}

internal sealed class ZetlStateStore
{
    private readonly ZetlStateStorage storage;
    private readonly string sessionId;

    public ZetlStateStore(string? statePath = null, string? sessionId = null)
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

        storage = new ZetlStateStorage(rootDirectory, legacyStatePath);
        this.sessionId = string.IsNullOrWhiteSpace(sessionId) ? NewId() : sessionId;
        State = storage.Load();
        NormalizeLoadedState();
    }

    public ZetlState State { get; private set; }

    // Defaults applied to newly created projects and buckets. Set from app
    // settings; falls back to the built-in Standard defaults.
    public ZetlBucketDefaults Defaults { get; set; } = ZetlBucketDefaults.Standard;

    public string SessionId => sessionId;

    public event EventHandler? Changed;

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
        return project?.Buckets.FirstOrDefault(bucket => bucket.Id == project.ActiveBucketId);
    }

    public ZetlProject CreateProject(string name, IEnumerable<string> bucketNames, string? activeBucketName = null, bool shifted = false)
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
            ActiveBucketId = activeBucket.Id,
            Buckets = buckets
        };

        State.Projects.Add(project);
        SetActiveProjectId(project.Id, shifted);
        PersistProject(project, workspace: true);
        return project;
    }

    public ZetlProject GetOrCreateDefaultProject(bool shifted = false)
    {
        if (GetActiveProject(shifted) is { } activeProject)
        {
            return activeProject;
        }

        var defaultName = DefaultProjectName(shifted);
        var existingProject = ConsolidateProjectsNamed(defaultName);
        if (existingProject is not null)
        {
            SetActiveProjectId(existingProject.Id, shifted);
            PersistWorkspace();
            return existingProject;
        }

        var defaultBuckets = Defaults.ProjectBuckets.Count > 0 ? Defaults.ProjectBuckets : ["Inbox", "Scratch"];
        return CreateProject(defaultName, defaultBuckets, defaultBuckets[0], shifted);
    }

    public void ConsolidateDefaultProject(bool shifted = false)
    {
        // ConsolidateProjectsNamed persists the merged project and removes the
        // duplicates' files itself, so there is nothing extra to save here.
        ConsolidateProjectsNamed(DefaultProjectName(shifted));
    }

    public void UpdateProjectName(ZetlProject project, string name, bool shifted = false)
    {
        project.Name = NormalizeName(name, DefaultProjectName(shifted));
        PersistProject(project);
    }

    public void ClearActiveProject(bool shifted = false)
    {
        SetActiveProjectId(null, shifted);
        PersistWorkspace();
    }

    public void DeleteProject(string projectId)
    {
        var project = State.Projects.FirstOrDefault(item => item.Id == projectId);
        if (project is null)
        {
            return;
        }

        State.Projects.Remove(project);
        storage.RemoveProject(projectId);
        if (State.ActiveProjectId == projectId)
        {
            State.ActiveProjectId = State.Projects.FirstOrDefault()?.Id;
        }

        if (State.ShiftActiveProjectId == projectId)
        {
            State.ShiftActiveProjectId = State.Projects.FirstOrDefault()?.Id;
        }

        PersistWorkspace();
    }

    // setActive controls whether the new/found bucket becomes the project's
    // active bucket. Compiling into a bucket passes false so it never reshuffles
    // the destination project's active bucket (which may not even be the project
    // you are working in).
    public ZetlBucket AddBucket(ZetlProject project, string name, string? parentBucketId = null, bool setActive = true)
    {
        var bucket = CreateBucket(NormalizeName(name, "New Bucket"));
        ApplyBucketDefaults(bucket);
        bucket.ParentBucketId = project.Buckets.Any(item => item.Id == parentBucketId)
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

    public ZetlBucket GetOrCreateBucket(ZetlProject project, string name, bool setActive = true)
    {
        var normalizedName = NormalizeName(name, "New Bucket");
        var bucket = project.Buckets.FirstOrDefault(item =>
            string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
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

    public void UpdateBucketName(ZetlBucket bucket, string name)
    {
        if (IsScratchBucket(bucket))
        {
            return;
        }

        bucket.Name = NormalizeName(name, "Bucket");
        PersistBucket(bucket);
    }

    public void DeleteBucket(ZetlProject project, string bucketId)
    {
        var bucket = project.Buckets.FirstOrDefault(item => item.Id == bucketId);
        if (bucket is null || IsScratchBucket(bucket))
        {
            return;
        }

        var idsToRemove = GetBucketAndDescendantIds(project, bucket.Id);
        project.Buckets.RemoveAll(item => idsToRemove.Contains(item.Id));
        EnsureScratchBucket(project.Buckets);
        if (project.ActiveBucketId is null
            || idsToRemove.Contains(project.ActiveBucketId)
            || project.Buckets.All(item => item.Id != project.ActiveBucketId))
        {
            project.ActiveBucketId = project.Buckets.First().Id;
        }

        if (project.QuickNoteBucketId is not null
            && (idsToRemove.Contains(project.QuickNoteBucketId)
                || project.Buckets.All(item => item.Id != project.QuickNoteBucketId)))
        {
            project.QuickNoteBucketId = null;
        }

        PersistProject(project);
    }

    public ZetlNote AddNote(ZetlBucket bucket, string text, string source)
    {
        var note = new ZetlNote
        {
            Id = NewId(),
            Text = text.Trim(),
            Source = source,
            SessionId = sessionId,
            CreatedAtUtc = DateTime.UtcNow
        };
        bucket.Notes.Add(note);
        PersistBucket(bucket);
        return note;
    }

    // Adds one note per non-blank text, preserving order, with a single save.
    // Used by a structured compile-to-bucket that keeps notes separate instead
    // of flattening them into one combined note.
    public IReadOnlyList<ZetlNote> AddNotes(ZetlBucket bucket, IEnumerable<string> texts, string source)
    {
        var added = new List<ZetlNote>();
        foreach (var text in texts)
        {
            var trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var note = new ZetlNote
            {
                Id = NewId(),
                Text = trimmed,
                Source = source,
                SessionId = sessionId,
                CreatedAtUtc = DateTime.UtcNow
            };
            bucket.Notes.Add(note);
            added.Add(note);
        }

        if (added.Count > 0)
        {
            PersistBucket(bucket);
        }

        return added;
    }

    public void DeleteNote(ZetlBucket bucket, string noteId)
    {
        var note = bucket.Notes.FirstOrDefault(item => item.Id == noteId);
        if (note is null)
        {
            return;
        }

        bucket.Notes.Remove(note);
        PersistBucket(bucket);
    }

    public void UpdateNote(ZetlNote note, string text)
    {
        note.Text = text.Trim();
        PersistNote(note);
    }

    public void SetActiveProject(string projectId, bool shifted = false)
    {
        if (State.Projects.Any(project => project.Id == projectId))
        {
            SetActiveProjectId(projectId, shifted);
            PersistWorkspace();
        }
    }

    public void SetActiveBucket(ZetlProject project, string bucketId)
    {
        if (project.Buckets.Any(bucket => bucket.Id == bucketId))
        {
            project.ActiveBucketId = bucketId;
            PersistProject(project);
        }
    }

    public ZetlBucket GetQuickNoteBucket(ZetlProject project)
    {
        var bucket = project.Buckets.FirstOrDefault(bucket => bucket.Id == project.QuickNoteBucketId);
        return bucket ?? GetScratchBucket(project);
    }

    public void SetQuickNoteBucket(ZetlProject project, string bucketId)
    {
        if (project.Buckets.Any(bucket => bucket.Id == bucketId))
        {
            project.QuickNoteBucketId = bucketId;
            PersistProject(project);
        }
    }

    public void SetBucketPopMode(ZetlBucket bucket, bool popMode)
    {
        if (IsFifoBucket(bucket))
        {
            bucket.PopMode = false;
            PersistBucket(bucket);
            return;
        }

        bucket.PopMode = popMode;
        PersistBucket(bucket);
    }

    public void SetBucketKind(ZetlBucket bucket, string kind)
    {
        bucket.Kind = NormalizeBucketKind(kind);
        if (IsFifoBucket(bucket))
        {
            bucket.PopMode = false;
        }

        PersistBucket(bucket);
    }

    public void UpdateBucketSettings(
        ZetlBucket bucket,
        string name,
        string defaultKind,
        string defaultCompileMode,
        string defaultStartingText,
        int defaultTsvRowLength)
    {
        // Scratch keeps its name; everything else about it stays editable.
        if (!IsScratchBucket(bucket))
        {
            bucket.Name = NormalizeName(name, "Bucket");
        }

        bucket.DefaultKind = NormalizeBucketKind(defaultKind);
        bucket.Kind = bucket.DefaultKind;
        bucket.DefaultCompileMode = NormalizeCompileMode(defaultCompileMode);
        bucket.DefaultStartingText = (defaultStartingText ?? "").Trim();
        bucket.DefaultTsvRowLength = Math.Max(1, defaultTsvRowLength);
        if (IsFifoBucket(bucket))
        {
            bucket.PopMode = false;
        }

        PersistBucket(bucket);
    }

    public void ToggleActiveBucketPopMode(bool shifted = false)
    {
        var bucket = GetActiveBucket(shifted);
        if (bucket is null || IsFifoBucket(bucket))
        {
            return;
        }

        bucket.PopMode = !bucket.PopMode;
        PersistBucket(bucket);
    }

    public ZetlBucket GetScratchBucket(ZetlProject project)
    {
        EnsureScratchBucket(project.Buckets);
        var scratch = project.Buckets.First(bucket => string.Equals(bucket.Name, "Scratch", StringComparison.OrdinalIgnoreCase));
        if (project.ActiveBucketId is null)
        {
            project.ActiveBucketId = scratch.Id;
        }

        return scratch;
    }

    public bool TryPopLastMatchingActiveNote(string text, bool shifted = false)
    {
        return TryPopLastMatchingActiveNote(text, shifted, out _, out _);
    }

    public bool TryPopLastMatchingActiveNote(string text, bool shifted, out ZetlBucket? bucket, out ZetlNote? note)
    {
        bucket = GetActiveBucket(shifted);
        note = null;
        if (bucket is null || IsFifoBucket(bucket) || !bucket.PopMode || bucket.Notes.Count == 0)
        {
            return false;
        }

        var last = bucket.Notes.LastOrDefault(IsCurrentSessionNote);
        if (last is null)
        {
            return false;
        }

        if (!string.Equals(last.Text, text.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        bucket.Notes.Remove(last);
        note = last;
        PersistBucket(bucket);
        return true;
    }

    public bool TryPeekNextFifoNote(ZetlBucket? bucket, out ZetlNote? note)
    {
        if (bucket is null || !IsFifoBucket(bucket))
        {
            note = null;
            return false;
        }

        note = bucket.Notes.FirstOrDefault(item => IsCurrentSessionNote(item) && !string.IsNullOrWhiteSpace(item.Text));
        return note is not null;
    }

    public bool TryConsumeFifoNote(ZetlBucket bucket, string noteId)
    {
        return TryConsumeFifoNote(bucket, noteId, out _);
    }

    public bool TryConsumeFifoNote(ZetlBucket bucket, string noteId, out ZetlNote? consumedNote)
    {
        if (!IsFifoBucket(bucket))
        {
            consumedNote = null;
            return false;
        }

        var note = bucket.Notes.FirstOrDefault(item => item.Id == noteId && IsCurrentSessionNote(item));
        if (note is null)
        {
            consumedNote = null;
            return false;
        }

        bucket.Notes.Remove(note);
        consumedNote = note;
        PersistBucket(bucket);
        return true;
    }

    public bool TryConsumeFifoNoteToReview(ZetlProject project, ZetlBucket bucket, string noteId, out ZetlBucket? reviewBucket)
    {
        return TryConsumeFifoNoteToReview(project, bucket, noteId, out reviewBucket, out _, out _);
    }

    public bool TryConsumeFifoNoteToReview(
        ZetlProject project,
        ZetlBucket bucket,
        string noteId,
        out ZetlBucket? reviewBucket,
        out ZetlNote? consumedNote,
        out ZetlNote? reviewNote)
    {
        reviewBucket = null;
        consumedNote = null;
        reviewNote = null;
        if (!IsFifoBucket(bucket))
        {
            return false;
        }

        var note = bucket.Notes.FirstOrDefault(item => item.Id == noteId && IsCurrentSessionNote(item));
        if (note is null)
        {
            return false;
        }

        bucket.Notes.Remove(note);
        consumedNote = note;
        if (!string.IsNullOrWhiteSpace(note.Text))
        {
            reviewBucket = GetOrCreateFifoReviewBucket(project, bucket);
            reviewNote = new ZetlNote
            {
                Id = NewId(),
                Text = note.Text.Trim(),
                Source = "replay",
                SessionId = sessionId,
                CreatedAtUtc = DateTime.UtcNow
            };
            reviewBucket.Notes.Add(reviewNote);
        }

        PersistProject(project);
        return true;
    }

    public void RestoreNote(ZetlBucket bucket, ZetlNote note, bool insertAtFront = false)
    {
        if (bucket.Notes.Any(item => item.Id == note.Id))
        {
            return;
        }

        if (insertAtFront)
        {
            bucket.Notes.Insert(0, note);
        }
        else
        {
            bucket.Notes.Add(note);
        }

        PersistBucket(bucket);
    }

    public void RestoreFifoConsumedNote(ZetlBucket bucket, ZetlNote note, ZetlBucket? reviewBucket, string? reviewNoteId)
    {
        bucket.Kind = "Replay";
        bucket.PopMode = false;
        if (reviewBucket is not null && reviewNoteId is not null)
        {
            reviewBucket.Notes.RemoveAll(item => item.Id == reviewNoteId);
        }

        if (bucket.Notes.All(item => item.Id != note.Id))
        {
            bucket.Notes.Insert(0, note);
        }

        PersistBucket(bucket);
    }

    public string CompilePlainText(ZetlProject project, IEnumerable<ZetlBucket> selectedBuckets)
    {
        var parts = new List<string> { project.Name.Trim(), "" };
        foreach (var bucket in selectedBuckets)
        {
            parts.Add(bucket.Name.Trim());
            parts.AddRange(bucket.Notes.Select(note => note.Text));
            parts.Add("");
        }

        return string.Join(Environment.NewLine, parts).TrimEnd();
    }

    public string CompilePlainTextFromNotes(ZetlProject project, IEnumerable<NoteDisplayItem> selectedNotes)
    {
        var parts = new List<string> { project.Name.Trim(), "" };
        foreach (var group in selectedNotes.GroupBy(item => item.Bucket))
        {
            parts.Add(group.Key.Name.Trim());
            parts.AddRange(group.Select(item => item.Note.Text));
            parts.Add("");
        }

        return string.Join(Environment.NewLine, parts).TrimEnd();
    }

    public string CompileUnformattedFromNotes(IEnumerable<NoteDisplayItem> selectedNotes)
    {
        return string.Join(
            Environment.NewLine,
            selectedNotes
                .Select(item => item.Note.Text.Trim())
                .Where(text => text.Length > 0));
    }

    public string CompileTsvFromNotes(ZetlProject project, IEnumerable<NoteDisplayItem> selectedNotes, int rowLength)
    {
        var normalizedRowLength = Math.Max(1, rowLength);
        var parts = new List<string> { project.Name.Trim() };
        foreach (var group in selectedNotes.GroupBy(item => item.Bucket))
        {
            parts.Add(group.Key.Name.Trim());
            var headers = GetBucketHeaderCells(group.Key);
            if (headers.Count > 0)
            {
                parts.Add(string.Join('\t', headers));
            }

            var cells = group
                .Select(item => NormalizeTsvCell(item.Note.Text))
                .Where(text => text.Length > 0)
                .ToList();
            for (var i = 0; i < cells.Count; i += normalizedRowLength)
            {
                parts.Add(string.Join('\t', cells.Skip(i).Take(normalizedRowLength)));
            }

            parts.Add("");
        }

        return string.Join(Environment.NewLine, parts).TrimEnd();
    }

    public int GetBucketTsvRowLength(ZetlBucket bucket)
    {
        var headerLength = GetBucketHeaderCells(bucket).Count;
        return headerLength > 0 ? headerLength : Math.Max(1, bucket.DefaultTsvRowLength);
    }

    public IReadOnlyList<BucketDisplayItem> GetBucketDisplayItems(ZetlProject project)
    {
        var result = new List<BucketDisplayItem>();
        AddChildren(parentId: null, depth: 0);

        foreach (var bucket in project.Buckets.OrderBy(bucket => bucket.Name, StringComparer.OrdinalIgnoreCase))
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
                .Where(bucket => bucket.ParentBucketId == parentId)
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
    public IReadOnlyList<NoteDisplayItem> GetNoteDisplayItems(ZetlProject project, IReadOnlyList<ZetlBucket>? bucketScope = null, bool currentSessionOnly = false)
    {
        var scopedBucketIds = bucketScope?.Select(bucket => bucket.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<NoteDisplayItem>();
        foreach (var bucketItem in GetBucketDisplayItems(project)
            .Where(item => scopedBucketIds is null || scopedBucketIds.Contains(item.Bucket.Id)))
        {
            foreach (var note in bucketItem.Bucket.Notes.Where(note => IsCompilableNote(note, currentSessionOnly)))
            {
                result.Add(new NoteDisplayItem(bucketItem.Bucket, note, $"{bucketItem.Label.Trim()}: {PreviewText(note.Text)}"));
            }
        }

        return result;
    }

    public bool TryGetLastNoteDisplayItem(ZetlProject project, IReadOnlyList<ZetlBucket>? bucketScope, out NoteDisplayItem? note, bool currentSessionOnly = false)
    {
        note = GetNoteDisplayItems(project, bucketScope, currentSessionOnly)
            .OrderByDescending(item => item.Note.CreatedAtUtc)
            .FirstOrDefault();
        return note is not null;
    }

    public bool HasCompilableNotes(ZetlProject project, bool currentSessionOnly = false)
    {
        return project.Buckets.Any(bucket => bucket.Notes.Any(note => IsCompilableNote(note, currentSessionOnly)));
    }

    private bool IsCompilableNote(ZetlNote note, bool currentSessionOnly)
    {
        return (!currentSessionOnly || IsCurrentSessionNote(note)) && !string.IsNullOrWhiteSpace(note.Text);
    }

    public bool TryGetScratchCompileTarget(out ZetlProject? project, out ZetlBucket? scratchBucket, bool shifted = false)
    {
        var defaultName = DefaultProjectName(shifted);
        var candidates = State.Projects
            .OrderByDescending(item => string.Equals(item.Name, defaultName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var scratch = candidate.Buckets.FirstOrDefault(bucket =>
                string.Equals(bucket.Name, "Scratch", StringComparison.OrdinalIgnoreCase)
                && bucket.Notes.Any(note => IsCurrentSessionNote(note) && !string.IsNullOrWhiteSpace(note.Text)));
            if (scratch is not null)
            {
                project = candidate;
                scratchBucket = scratch;
                return true;
            }
        }

        project = null;
        scratchBucket = null;
        return false;
    }

    // Persist a single project's file, optionally rewriting the workspace
    // pointers alongside it. Note capture, edits, and toggles all touch exactly
    // one project, so scoping the save here keeps each write to the one file
    // that changed instead of rewriting every project on disk.
    private void PersistProject(ZetlProject project, bool workspace = false)
    {
        NormalizeProject(project);
        storage.WriteProject(project);
        if (workspace)
        {
            NormalizeWorkspacePointers();
            storage.WriteWorkspace(BuildWorkspaceFile());
        }

        Changed?.Invoke(this, EventArgs.Empty);
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

    private void PersistNote(ZetlNote note)
    {
        var owner = OwnerProjectOfNote(note);
        if (owner is null)
        {
            SaveAll();
            return;
        }

        PersistProject(owner);
    }

    private void PersistWorkspace()
    {
        NormalizeWorkspacePointers();
        storage.WriteWorkspace(BuildWorkspaceFile());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Full flush: every project plus the workspace pointers. Used as a safety
    // net when a mutation can't resolve which project it touched.
    private void SaveAll()
    {
        foreach (var project in State.Projects)
        {
            NormalizeProject(project);
            storage.WriteProject(project);
        }

        NormalizeWorkspacePointers();
        storage.WriteWorkspace(BuildWorkspaceFile());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private ZetlProject? OwnerProject(ZetlBucket bucket)
    {
        return State.Projects.FirstOrDefault(project => project.Buckets.Any(item => item.Id == bucket.Id));
    }

    private ZetlProject? OwnerProjectOfNote(ZetlNote note)
    {
        return State.Projects.FirstOrDefault(project =>
            project.Buckets.Any(bucket => bucket.Notes.Any(item => item.Id == note.Id)));
    }

    private ZetlWorkspaceFile BuildWorkspaceFile()
    {
        return new ZetlWorkspaceFile
        {
            Version = Math.Max(State.Version, 1),
            ActiveProjectId = State.ActiveProjectId,
            ShiftActiveProjectId = State.ShiftActiveProjectId
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
    }

    private void NormalizeProject(ZetlProject project)
    {
        project.Id = string.IsNullOrWhiteSpace(project.Id) ? NewId() : project.Id;
        project.Name = NormalizeName(project.Name, DefaultProjectName());
        project.Buckets ??= new List<ZetlBucket>();
        EnsureScratchBucket(project.Buckets);
        foreach (var bucket in project.Buckets)
        {
            bucket.Id = string.IsNullOrWhiteSpace(bucket.Id) ? NewId() : bucket.Id;
            bucket.Name = NormalizeName(bucket.Name, "Bucket");
            if (bucket.ParentBucketId == bucket.Id
                || project.Buckets.All(candidate => candidate.Id != bucket.ParentBucketId))
            {
                bucket.ParentBucketId = null;
            }

            if (bucket.FifoReviewBucketId == bucket.Id
                || project.Buckets.All(candidate => candidate.Id != bucket.FifoReviewBucketId))
            {
                bucket.FifoReviewBucketId = null;
            }

            bucket.Kind = NormalizeBucketKind(bucket.Kind);
            bucket.DefaultKind = NormalizeBucketKind(string.IsNullOrWhiteSpace(bucket.DefaultKind) ? bucket.Kind : bucket.DefaultKind);
            bucket.DefaultCompileMode = NormalizeCompileMode(bucket.DefaultCompileMode);
            bucket.DefaultStartingText ??= "";
            bucket.DefaultTsvRowLength = bucket.DefaultTsvRowLength <= 0 ? 5 : bucket.DefaultTsvRowLength;
            if (IsFifoBucket(bucket))
            {
                bucket.PopMode = false;
            }
            bucket.Notes ??= new List<ZetlNote>();
            foreach (var note in bucket.Notes)
            {
                note.Id = string.IsNullOrWhiteSpace(note.Id) ? NewId() : note.Id;
                note.Text ??= "";
                note.Source ??= "";
                if (note.CreatedAtUtc == default)
                {
                    note.CreatedAtUtc = DateTime.UtcNow;
                }
            }
        }

        if (project.Buckets.All(bucket => bucket.Id != project.ActiveBucketId))
        {
            project.ActiveBucketId = project.Buckets.First().Id;
        }

        if (project.QuickNoteBucketId is not null
            && project.Buckets.All(bucket => bucket.Id != project.QuickNoteBucketId))
        {
            project.QuickNoteBucketId = null;
        }
    }

    private void NormalizeWorkspacePointers()
    {
        State.Version = Math.Max(State.Version, 1);
        if (State.ActiveProjectId is not null
            && State.Projects.All(project => project.Id != State.ActiveProjectId))
        {
            State.ActiveProjectId = null;
        }

        if (State.ShiftActiveProjectId is not null
            && State.Projects.All(project => project.Id != State.ShiftActiveProjectId))
        {
            State.ShiftActiveProjectId = null;
        }
    }

    private static ZetlBucket CreateBucket(string name)
    {
        return new ZetlBucket
        {
            Id = NewId(),
            Name = NormalizeName(name, "Bucket"),
            Kind = "Standard",
            DefaultKind = "Standard",
            DefaultCompileMode = "Formatted",
            DefaultTsvRowLength = 5
        };
    }

    // Stamp a freshly created bucket with the user's default compile mode and
    // TSV row length.
    private void ApplyBucketDefaults(ZetlBucket bucket)
    {
        bucket.DefaultCompileMode = NormalizeCompileMode(Defaults.CompileMode);
        bucket.DefaultTsvRowLength = Math.Max(1, Defaults.TsvRowLength);
    }

    private ZetlProject? ConsolidateProjectsNamed(string projectName)
    {
        var matchingProjects = State.Projects
            .Where(project => string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matchingProjects.Count == 0)
        {
            return null;
        }

        var primary = matchingProjects[0];
        var merged = false;
        foreach (var duplicate in matchingProjects.Skip(1))
        {
            MergeProjectInto(primary, duplicate);
            State.Projects.Remove(duplicate);
            storage.RemoveProject(duplicate.Id);
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
            var sourceKind = NormalizeBucketKind(sourceBucket.Kind);
            if (targetBucket is null)
            {
                targetBucket = new ZetlBucket
                {
                    Id = NewId(),
                    Name = NormalizeName(sourceBucket.Name, "Bucket"),
                    ParentBucketId = parentBucket?.Id,
                    Kind = sourceKind,
                    DefaultKind = NormalizeBucketKind(sourceBucket.DefaultKind),
                    DefaultCompileMode = NormalizeCompileMode(sourceBucket.DefaultCompileMode),
                    DefaultStartingText = (sourceBucket.DefaultStartingText ?? "").Trim(),
                    DefaultTsvRowLength = sourceBucket.DefaultTsvRowLength <= 0 ? 5 : sourceBucket.DefaultTsvRowLength,
                    PopMode = !IsFifoKind(sourceKind) && sourceBucket.PopMode,
                    Notes = new List<ZetlNote>()
                };
                targetProject.Buckets.Add(targetBucket);
            }
            else
            {
                if (IsFifoKind(sourceKind))
                {
                    targetBucket.Kind = sourceKind;
                    targetBucket.PopMode = false;
                }
                else if (!IsFifoBucket(targetBucket))
                {
                    targetBucket.PopMode |= sourceBucket.PopMode;
                }

                targetBucket.DefaultCompileMode = NormalizeCompileMode(sourceBucket.DefaultCompileMode);
                if (string.IsNullOrWhiteSpace(targetBucket.DefaultStartingText))
                {
                    targetBucket.DefaultStartingText = (sourceBucket.DefaultStartingText ?? "").Trim();
                }

                targetBucket.DefaultTsvRowLength = sourceBucket.DefaultTsvRowLength <= 0 ? 5 : sourceBucket.DefaultTsvRowLength;
            }

            foreach (var note in sourceBucket.Notes)
            {
                targetBucket.Notes.Add(note);
            }

            bucketMap[sourceBucket.Id] = targetBucket;
        }

        foreach (var sourceBucket in sourceProject.Buckets)
        {
            if (sourceBucket.FifoReviewBucketId is not null
                && bucketMap.TryGetValue(sourceBucket.Id, out var targetBucket)
                && bucketMap.TryGetValue(sourceBucket.FifoReviewBucketId, out var targetReviewBucket)
                && targetBucket.Id != targetReviewBucket.Id)
            {
                targetBucket.FifoReviewBucketId = targetReviewBucket.Id;
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
        if (project.Buckets.All(bucket => bucket.Id != project.ActiveBucketId))
        {
            project.ActiveBucketId = project.Buckets.First().Id;
        }

        if (project.QuickNoteBucketId is not null
            && project.Buckets.All(bucket => bucket.Id != project.QuickNoteBucketId))
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
        if (buckets.Any(bucket => string.Equals(bucket.Name, "Scratch", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        buckets.Add(CreateBucket("Scratch"));
    }

    private static IEnumerable<string> NormalizeBucketNames(IEnumerable<string> bucketNames)
    {
        var names = bucketNames
            .Select(name => NormalizeName(name, ""))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count == 0 ? ["Inbox"] : names;
    }

    private static string NormalizeName(string? value, string fallback)
    {
        var normalized = (value ?? "").Trim();
        return normalized.Length == 0 ? fallback : normalized;
    }

    private bool IsCurrentSessionNote(ZetlNote note)
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
    }

    public static bool IsFifoBucket(ZetlBucket bucket)
    {
        return IsFifoKind(bucket.Kind);
    }

    // The Scratch bucket is special (always present, the quick-note default)
    // and currently cannot be renamed or deleted.
    public static bool IsScratchBucket(ZetlBucket bucket)
    {
        return string.Equals(bucket.Name, "Scratch", StringComparison.OrdinalIgnoreCase);
    }

    // Whether a raw kind string represents Replay Mode (accepts the legacy
    // "Fifo" value as well).
    public static bool IsReplayKind(string? kind)
    {
        return IsFifoKind(kind);
    }

    private static bool IsFifoKind(string? kind)
    {
        // "Replay" is the stored value; "Fifo" is the legacy value from older
        // state files, mapped forward to "Replay" by NormalizeBucketKind.
        return string.Equals(kind, "Replay", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "Fifo", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBucketKind(string? kind)
    {
        return IsFifoKind(kind) ? "Replay" : "Standard";
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

    private ZetlBucket GetOrCreateFifoReviewBucket(ZetlProject project, ZetlBucket fifoBucket)
    {
        if (fifoBucket.FifoReviewBucketId is not null)
        {
            var existingReviewBucket = project.Buckets.FirstOrDefault(bucket =>
                bucket.Id == fifoBucket.FifoReviewBucketId && bucket.Id != fifoBucket.Id);
            if (existingReviewBucket is not null)
            {
                return existingReviewBucket;
            }
        }

        var reviewBucketName = NormalizeName($"{fifoBucket.Name} Review", "Replay Review");
        var reviewBucket = project.Buckets.FirstOrDefault(bucket =>
            bucket.Id != fifoBucket.Id
            && string.Equals(bucket.Name, reviewBucketName, StringComparison.OrdinalIgnoreCase));
        if (reviewBucket is null)
        {
            reviewBucket = CreateBucket(reviewBucketName);
            project.Buckets.Add(reviewBucket);
        }

        reviewBucket.Kind = "Standard";
        reviewBucket.PopMode = false;
        fifoBucket.FifoReviewBucketId = reviewBucket.Id;
        return reviewBucket;
    }

    private static string PreviewText(string text)
    {
        var preview = text.ReplaceLineEndings(" ").Trim();
        return preview.Length <= 80 ? preview : $"{preview[..77]}...";
    }

    private static string NormalizeTsvCell(string text)
    {
        return text
            .ReplaceLineEndings(" ")
            .Replace('\t', ' ')
            .Trim();
    }

    private static IReadOnlyList<string> GetBucketHeaderCells(ZetlBucket bucket)
    {
        return (bucket.DefaultStartingText ?? "")
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Select(NormalizeTsvCell)
            .Where(text => text.Length > 0)
            .ToList();
    }

    private static string NewId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static string DefaultProjectName(bool shifted = false)
    {
        var name = DateTime.Now.ToString("yyyy-MM-dd");
        return shifted ? $"{name} Shift" : name;
    }
}
