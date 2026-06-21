using System.Runtime.CompilerServices;

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
    public long MetadataRevision { get; set; } = 1;
    public long ChangeSequence { get; set; }
    public string? ActiveBucketId { get; set; }
    public string? QuickNoteBucketId { get; set; }
    // The view document this project renders with by default (set by a creation
    // type, or chosen in Kastn). Null falls back to the first view.
    public string? DefaultViewId { get; set; }
    public List<ZetlBucket> Buckets { get; set; } = new();
}

internal sealed class ZetlBucket
{
    public string Id { get; set; } = "";
    public long Revision { get; set; } = 1;
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
    public const string TextKind = "Text";
    public const string ImageKind = "Image";

    public string Id { get; set; } = "";
    public long Revision { get; set; } = 1;
    public string ContentKind { get; set; } = TextKind;
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public ZetlImageAsset? Image { get; set; }
    public string Source { get; set; } = "";
    public string? SessionId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? DeletedFromBucketId { get; set; }
    public DateTime? DeletedAtUtc { get; set; }

    public ZetlCaptureOrigin? CaptureOrigin { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCaptureOrigin => CaptureOrigin?.HasDisplayValue == true;

    [System.Text.Json.Serialization.JsonIgnore]
    public string CaptureOriginLabel => CaptureOrigin?.FormatDisplay(CreatedAtUtc) ?? "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsImage => ContentKind == ImageKind && Image is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayText => IsImage
        ? string.IsNullOrWhiteSpace(Text)
            ? !string.IsNullOrWhiteSpace(Title)
                ? Title
                : $"Image · {Image!.Width}×{Image.Height} · {FormatBytes(Image.ByteLength)}"
            : Text
        : string.IsNullOrWhiteSpace(Title) ? Text : Title;

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024d):0.#} MB"
        : $"{Math.Max(1, bytes / 1024d):0.#} KB";
}

internal sealed class ZetlImageAsset
{
    public string RelativePath { get; set; } = "";
    public string? SourceUrl { get; set; }
    public string MimeType { get; set; } = "image/png";
    public int Width { get; set; }
    public int Height { get; set; }
    public long ByteLength { get; set; }
    public string Sha256 { get; set; } = "";
}

internal sealed record ZetlBucketDefaults(IReadOnlyList<string> ProjectBuckets, string CompileMode, int TsvRowLength)
{
    public static ZetlBucketDefaults Standard { get; } = new(new[] { "Inbox", "Scratch" }, "Formatted", 5);

    // The configured project buckets, or the built-in Inbox/Scratch fallback
    // when none are set. Centralizes the fallback several call sites inlined.
    public static IReadOnlyList<string> ResolveProjectBuckets(IReadOnlyList<string>? buckets)
    {
        return buckets is { Count: > 0 } ? buckets : Standard.ProjectBuckets;
    }

    public IReadOnlyList<string> ResolvedProjectBuckets => ResolveProjectBuckets(ProjectBuckets);
}

internal sealed class ZetlStateStore
{
    // Name of the dedicated activity-log project. It is never made active.
    public const string LogProjectName = "Zetl Logs";
    public const string DeletedBucketName = "Deleted";
    public const string DeletedBucketKind = "Deleted";

    private readonly ZetlStateStorage storage;
    private readonly string sessionId;
    private readonly Action<string>? log;

    // Current UI/runtime mutations and the Kastn command service share this
    // instance monitor. Synchronized public mutators therefore cannot interleave
    // their in-memory changes before an atomic project write.
    internal object MutationSyncRoot => this;

    public ZetlStateStore(string? statePath = null, string? sessionId = null, Action<string>? log = null)
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

        storage = new ZetlStateStorage(rootDirectory, legacyStatePath, log);
        this.log = log;
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

    [MethodImpl(MethodImplOptions.Synchronized)]
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

        var defaultBuckets = Defaults.ResolvedProjectBuckets;
        return CreateProject(defaultName, defaultBuckets, defaultBuckets[0], shifted);
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
    public void SetProjectDefaultView(ZetlProject project, string? viewId)
    {
        project.DefaultViewId = string.IsNullOrWhiteSpace(viewId) ? null : viewId.Trim();
        project.MetadataRevision++;
        PersistProject(project);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ClearActiveProject(bool shifted = false)
    {
        SetActiveProjectId(null, shifted);
        PersistWorkspace();
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
    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlBucket AddBucket(ZetlProject project, string name, string? parentBucketId = null, bool setActive = true)
    {
        var normalizedName = NormalizeName(name, "New Bucket");
        if (IsDeletedBucketName(normalizedName))
        {
            return GetDeletedBucket(project);
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
        if (IsDeletedBucketName(normalizedName))
        {
            return GetDeletedBucket(project);
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

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void UpdateBucketName(ZetlBucket bucket, string name)
    {
        if (IsScratchBucket(bucket) || IsDeletedBucket(bucket))
        {
            return;
        }

        bucket.Name = NormalizeName(name, "Bucket");
        bucket.Revision++;
        PersistBucket(bucket);
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
    public ZetlNote AddNote(
        ZetlBucket bucket,
        string text,
        string source,
        ZetlCaptureOrigin? captureOrigin) =>
        AddNote(bucket, text, source, null, null, captureOrigin);

    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlNote AddNote(
        ZetlBucket bucket,
        string text,
        string source,
        string? noteSessionId = null,
        DateTime? createdAtUtc = null,
        ZetlCaptureOrigin? captureOrigin = null,
        string? title = null)
    {
        var note = new ZetlNote
        {
            Id = NewId(),
            Title = (title ?? "").Trim(),
            Text = text.Trim(),
            Source = source,
            SessionId = noteSessionId ?? sessionId,
            CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow,
            CaptureOrigin = captureOrigin
        };
        bucket.Notes.Add(note);
        PersistBucket(bucket);
        return note;
    }

    public ZetlNote AddImageNote(
        ZetlProject project,
        ZetlBucket bucket,
        ZetlClipboardImage image,
        string source,
        ZetlCaptureOrigin? captureOrigin = null,
        string? caption = null,
        string? sourceUrl = null)
    {
        if (!project.Buckets.Any(item => item.Id == bucket.Id))
        {
            throw new InvalidOperationException("The image destination bucket does not belong to the project.");
        }

        if (image.PngBytes.Length == 0 || image.Width <= 0 || image.Height <= 0)
        {
            throw new InvalidDataException("The clipboard image is empty or has invalid dimensions.");
        }

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(image.PngBytes))
            .ToLowerInvariant();
        var relativePath = storage.WriteAsset(project, hash, ".png", image.PngBytes);
        var note = new ZetlNote
        {
            Id = NewId(),
            ContentKind = ZetlNote.ImageKind,
            Text = (caption ?? "").Trim(),
            Image = new ZetlImageAsset
            {
                RelativePath = relativePath,
                SourceUrl = sourceUrl,
                Width = image.Width,
                Height = image.Height,
                ByteLength = image.PngBytes.LongLength,
                Sha256 = hash
            },
            Source = source,
            SessionId = sessionId,
            CreatedAtUtc = DateTime.UtcNow,
            CaptureOrigin = captureOrigin
        };
        bucket.Notes.Add(note);
        PersistProject(project);
        return note;
    }

    public byte[]? ReadImageAsset(ZetlProject project, ZetlNote note)
    {
        return note.IsImage && note.Image is not null
            ? storage.ReadAsset(project, note.Image.RelativePath)
            : null;
    }

    public string? GetImageAssetPath(ZetlProject project, ZetlNote note)
    {
        return note.IsImage && note.Image is not null
            ? storage.GetAssetPath(project, note.Image.RelativePath)
            : null;
    }

    public IReadOnlyList<ZetlProjectAssetFile> GetProjectAssets(ZetlProject project) =>
        storage.GetAssets(project);

    // Adds one note per non-blank text, preserving order, with a single save.
    // Used by a structured compile-to-bucket that keeps notes separate instead
    // of flattening them into one combined note.
    [MethodImpl(MethodImplOptions.Synchronized)]
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

    // Appends activity-log lines as notes in a dedicated "Zetl Logs" project,
    // grouped into a per-day bucket. The log project is infrastructure: it is
    // never made the active project, so it cannot hijack a lane. Retention is
    // bounded (the day bucket is capped and stale day buckets are dropped) so the
    // file cannot grow without limit, and it saves once per call -- the caller
    // batches lines so logging stays off the per-keystroke path.
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void AppendLogNotes(IReadOnlyCollection<string> messages, int maxDayBuckets, int maxNotesPerBucket)
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

            bucket.Notes.Add(new ZetlNote
            {
                Id = NewId(),
                Text = trimmed,
                Source = "log",
                SessionId = sessionId,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        if (bucket.Notes.Count > maxNotesPerBucket)
        {
            bucket.Notes.RemoveRange(0, bucket.Notes.Count - maxNotesPerBucket);
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

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void UpdateNote(ZetlNote note, string text, string? title = null)
    {
        note.Text = text.Trim();
        if (title is not null)
        {
            note.Title = title.Trim();
        }
        note.Revision++;
        PersistNote(note);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetActiveProject(string projectId, bool shifted = false)
    {
        if (State.Projects.Any(project => project.Id == projectId))
        {
            SetActiveProjectId(projectId, shifted);
            PersistWorkspace();
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
            bucket.PopMode = false;
            bucket.Revision++;
            PersistBucket(bucket);
            return;
        }

        if (IsFifoBucket(bucket))
        {
            bucket.PopMode = false;
            bucket.Revision++;
            PersistBucket(bucket);
            return;
        }

        bucket.PopMode = popMode;
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

        bucket.Kind = NormalizeBucketKind(kind);
        if (IsFifoBucket(bucket))
        {
            bucket.PopMode = false;
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
            bucket.DefaultKind = NormalizeBucketKind(defaultKind);
            bucket.Kind = bucket.DefaultKind;
            bucket.DefaultCompileMode = NormalizeCompileMode(defaultCompileMode);
            bucket.DefaultStartingText = (defaultStartingText ?? "").Trim();
            bucket.DefaultTsvRowLength = Math.Max(1, defaultTsvRowLength);
            if (IsFifoBucket(bucket))
            {
                bucket.PopMode = false;
            }
        }
        else
        {
            bucket.DefaultKind = NormalizeBucketKind(defaultKind);
            bucket.Kind = bucket.DefaultKind;
            bucket.DefaultCompileMode = NormalizeCompileMode(defaultCompileMode);
            bucket.DefaultStartingText = (defaultStartingText ?? "").Trim();
            bucket.DefaultTsvRowLength = Math.Max(1, defaultTsvRowLength);
            if (IsFifoBucket(bucket))
            {
                bucket.PopMode = false;
            }
        }

        bucket.Revision++;
        PersistBucket(bucket);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void UpdateBucket(
        ZetlProject project,
        ZetlBucket bucket,
        string name,
        string? parentBucketId,
        string kind,
        string defaultKind,
        string defaultCompileMode,
        string defaultStartingText,
        int defaultTsvRowLength,
        bool popMode,
        string? replayReviewBucketId)
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

        if (!IsScratchBucket(bucket))
        {
            bucket.Name = NormalizeName(name, "Bucket");
        }

        bucket.ParentBucketId = parentBucketId != bucket.Id
            && project.Buckets.Any(item => item.Id == parentBucketId && !IsDeletedBucket(item))
                ? parentBucketId
                : null;
        bucket.Kind = NormalizeBucketKind(kind);
        bucket.DefaultKind = NormalizeBucketKind(defaultKind);
        bucket.DefaultCompileMode = NormalizeCompileMode(defaultCompileMode);
        bucket.DefaultStartingText = (defaultStartingText ?? "").Trim();
        bucket.DefaultTsvRowLength = Math.Max(1, defaultTsvRowLength);
        bucket.PopMode = !IsFifoBucket(bucket) && popMode;
        bucket.FifoReviewBucketId = replayReviewBucketId != bucket.Id
            && project.Buckets.Any(item => item.Id == replayReviewBucketId && !IsDeletedBucket(item))
                ? replayReviewBucketId
                : null;
        bucket.Revision++;
        PersistProject(project);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool MoveNote(ZetlProject project, ZetlNote note, ZetlBucket destination)
    {
        var source = project.Buckets.FirstOrDefault(bucket =>
            bucket.Notes.Any(item => item.Id == note.Id));
        if (source is null
            || project.Buckets.All(bucket => bucket.Id != destination.Id)
            || source.Id == destination.Id)
        {
            return false;
        }

        source.Notes.RemoveAll(item => item.Id == note.Id);
        destination.Notes.Add(note);
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
    public bool ReorderNote(ZetlProject project, ZetlNote note, string? beforeNoteId)
    {
        var bucket = project.Buckets.FirstOrDefault(bucket =>
            bucket.Notes.Any(item => item.Id == note.Id));
        if (bucket is null)
        {
            return false;
        }

        var currentIndex = bucket.Notes.FindIndex(item => item.Id == note.Id);
        int targetIndex;
        if (beforeNoteId is null)
        {
            targetIndex = bucket.Notes.Count;
        }
        else
        {
            var anchorIndex = bucket.Notes.FindIndex(item => item.Id == beforeNoteId);
            if (anchorIndex < 0)
            {
                return false;
            }

            targetIndex = anchorIndex;
        }

        bucket.Notes.RemoveAt(currentIndex);
        if (targetIndex > currentIndex)
        {
            targetIndex--;
        }

        targetIndex = Math.Clamp(targetIndex, 0, bucket.Notes.Count);
        bucket.Notes.Insert(targetIndex, note);
        note.Revision++;
        PersistProject(project);
        return true;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ToggleActiveBucketPopMode(bool shifted = false)
    {
        var bucket = GetActiveBucket(shifted);
        if (bucket is null || IsFifoBucket(bucket))
        {
            return;
        }

        bucket.PopMode = !bucket.PopMode;
        bucket.Revision++;
        PersistBucket(bucket);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlBucket GetScratchBucket(ZetlProject project)
    {
        EnsureScratchBucket(project.Buckets);
        var scratch = project.Buckets.First(bucket => string.Equals(bucket.Name, "Scratch", StringComparison.OrdinalIgnoreCase));
        if (project.ActiveBucketId is null || project.Buckets.Any(bucket => bucket.Id == project.ActiveBucketId && IsDeletedBucket(bucket)))
        {
            project.ActiveBucketId = scratch.Id;
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
    public bool TryPopLastMatchingActiveNote(string text, bool shifted = false)
    {
        return TryPopLastMatchingActiveNote(text, shifted, out _, out _);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
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

        last.Revision++;
        bucket.Notes.Remove(last);
        note = last;
        PersistBucket(bucket);
        return true;
    }

    public bool TryPopLastMatchingActiveImage(
        string sha256,
        bool shifted,
        out ZetlBucket? bucket,
        out ZetlNote? note)
    {
        bucket = GetActiveBucket(shifted);
        note = null;
        if (bucket is null || IsFifoBucket(bucket) || !bucket.PopMode)
        {
            return false;
        }

        var last = bucket.Notes.LastOrDefault(IsCurrentSessionNote);
        if (last?.IsImage != true
            || last.Image is null
            || !string.Equals(last.Image.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
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

        note = bucket.Notes.FirstOrDefault(item =>
            IsCurrentSessionNote(item)
            && (item.IsImage || !string.IsNullOrWhiteSpace(item.Text)));
        return note is not null;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryConsumeFifoNote(ZetlBucket bucket, string noteId)
    {
        return TryConsumeFifoNote(bucket, noteId, out _);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
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

        note.Revision++;
        bucket.Notes.Remove(note);
        consumedNote = note;
        PersistBucket(bucket);
        return true;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryConsumeFifoNoteToReview(ZetlProject project, ZetlBucket bucket, string noteId, out ZetlBucket? reviewBucket)
    {
        return TryConsumeFifoNoteToReview(project, bucket, noteId, out reviewBucket, out _, out _);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
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

        note.Revision++;
        bucket.Notes.Remove(note);
        consumedNote = note;
        if (note.IsImage || !string.IsNullOrWhiteSpace(note.Text))
        {
            reviewBucket = GetOrCreateFifoReviewBucket(project, bucket);
            reviewNote = new ZetlNote
            {
                Id = NewId(),
                ContentKind = note.ContentKind,
                Text = note.Text.Trim(),
                Image = note.Image is null
                    ? null
                    : new ZetlImageAsset
                    {
                        RelativePath = note.Image.RelativePath,
                        SourceUrl = note.Image.SourceUrl,
                        MimeType = note.Image.MimeType,
                        Width = note.Image.Width,
                        Height = note.Image.Height,
                        ByteLength = note.Image.ByteLength,
                        Sha256 = note.Image.Sha256
                    },
                Source = "replay",
                SessionId = sessionId,
                CreatedAtUtc = DateTime.UtcNow,
                CaptureOrigin = note.CaptureOrigin
            };
            reviewBucket.Notes.Add(reviewNote);
        }

        PersistProject(project);
        return true;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
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

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RestoreFifoConsumedNote(ZetlBucket bucket, ZetlNote note, ZetlBucket? reviewBucket, string? reviewNoteId)
    {
        bucket.Kind = "Replay";
        bucket.PopMode = false;
        bucket.Revision++;
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
        foreach (var bucket in selectedBuckets.Where(bucket => !IsDeletedBucket(bucket)))
        {
            var depth = BucketDepth(bucket, project.Buckets);
            parts.Add(IndentedLine(bucket.Name.Trim(), depth));
            parts.AddRange(bucket.Notes
                .Where(note => !note.IsImage)
                .Select(note => IndentedText(note.Text, depth + 1)));
            parts.Add("");
        }

        return string.Join(Environment.NewLine, parts).TrimEnd();
    }

    public string CompilePlainTextFromNotes(ZetlProject project, IEnumerable<NoteDisplayItem> selectedNotes)
    {
        var parts = new List<string> { project.Name.Trim(), "" };
        foreach (var group in selectedNotes.GroupBy(item => item.Bucket))
        {
            var depth = BucketDepth(group.Key, project.Buckets);
            parts.Add(IndentedLine(group.Key.Name.Trim(), depth));
            parts.AddRange(group
                .Where(item => !item.Note.IsImage)
                .Select(item => IndentedText(item.Note.Text, depth + 1)));
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
        return project.Buckets.Any(bucket =>
            !IsDeletedBucket(bucket)
            && bucket.Notes.Any(note => IsCompilableNote(note, currentSessionOnly)));
    }

    private bool IsCompilableNote(ZetlNote note, bool currentSessionOnly)
    {
        return (!currentSessionOnly || IsCurrentSessionNote(note)) && !string.IsNullOrWhiteSpace(note.Text);
    }

    // The project most recently written to (its latest note), ignoring the
    // Zetl Logs infrastructure project, which is appended to constantly. Used to
    // land the Board on the last project you actually touched when no project is
    // active.
    public ZetlProject? GetMostRecentlyWrittenProject()
    {
        return State.Projects
            .Where(project => !string.Equals(project.Name, LogProjectName, StringComparison.OrdinalIgnoreCase))
            .Select(project => new
            {
                project,
                latest = project.Buckets
                    .Where(bucket => !IsDeletedBucket(bucket))
                    .SelectMany(bucket => bucket.Notes)
                    .Select(note => (DateTime?)note.CreatedAtUtc)
                    .Max()
            })
            .Where(item => item.latest is not null)
            .OrderByDescending(item => item.latest)
            .Select(item => item.project)
            .FirstOrDefault();
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
        var previousSequence = project.ChangeSequence;
        project.ChangeSequence = Math.Max(previousSequence, 0) + 1;
        try
        {
            storage.WriteProject(project);
        }
        catch
        {
            project.ChangeSequence = previousSequence;
            throw;
        }

        if (workspace)
        {
            NormalizeWorkspacePointers();
            storage.WriteWorkspace(BuildWorkspaceFile());
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
        RaiseChanged();
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
        RaiseChanged();
    }

    private void RaiseProjectPersisted(ZetlProjectPersistedEventArgs args)
    {
        if (ProjectPersisted is null)
        {
            return;
        }

        foreach (EventHandler<ZetlProjectPersistedEventArgs> handler in ProjectPersisted.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Project persistence subscriber failed: {ex.Message}");
            }
        }
    }

    private void RaiseChanged()
    {
        if (Changed is null)
        {
            return;
        }

        foreach (EventHandler handler in Changed.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                log?.Invoke($"State change subscriber failed: {ex.Message}");
            }
        }
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
        project.MetadataRevision = Math.Max(project.MetadataRevision, 1);
        project.ChangeSequence = Math.Max(project.ChangeSequence, 0);
        project.Buckets ??= new List<ZetlBucket>();
        EnsureScratchBucket(project.Buckets);
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

            if (bucket.FifoReviewBucketId == bucket.Id
                || project.Buckets.All(candidate => candidate.Id != bucket.FifoReviewBucketId || IsDeletedBucket(candidate)))
            {
                bucket.FifoReviewBucketId = null;
            }

            bucket.Kind = NormalizeBucketKind(bucket.Kind);
            bucket.DefaultKind = NormalizeBucketKind(string.IsNullOrWhiteSpace(bucket.DefaultKind) ? bucket.Kind : bucket.DefaultKind);
            bucket.DefaultCompileMode = NormalizeCompileMode(bucket.DefaultCompileMode);
            bucket.DefaultStartingText ??= "";
            bucket.DefaultTsvRowLength = bucket.DefaultTsvRowLength <= 0 ? 5 : bucket.DefaultTsvRowLength;
            if (IsDeletedBucket(bucket) || IsDeletedBucketName(bucket.Name))
            {
                EnsureDeletedBucketShape(bucket);
            }

            if (IsFifoBucket(bucket))
            {
                bucket.PopMode = false;
            }
            bucket.Notes ??= new List<ZetlNote>();
            foreach (var note in bucket.Notes)
            {
                note.Id = string.IsNullOrWhiteSpace(note.Id) ? NewId() : note.Id;
                note.Revision = Math.Max(note.Revision, 1);
                note.Title ??= "";
                note.Text ??= "";
                note.ContentKind = note.Image is not null
                    ? ZetlNote.ImageKind
                    : ZetlNote.TextKind;
                note.Source ??= "";
                if (note.CreatedAtUtc == default)
                {
                    note.CreatedAtUtc = DateTime.UtcNow;
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

    private static ZetlBucket? FirstActiveWorkflowBucket(ZetlProject project)
    {
        EnsureScratchBucket(project.Buckets);
        return project.Buckets.FirstOrDefault(bucket => !IsDeletedBucket(bucket));
    }

    private static void EnsureDeletedBucketShape(ZetlBucket bucket)
    {
        bucket.Name = DeletedBucketName;
        bucket.ParentBucketId = null;
        bucket.Kind = DeletedBucketKind;
        bucket.DefaultKind = DeletedBucketKind;
        bucket.PopMode = false;
        bucket.FifoReviewBucketId = null;
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
            CopyImageAssets(primary, duplicate);
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

    private void CopyImageAssets(ZetlProject targetProject, ZetlProject sourceProject)
    {
        foreach (var note in sourceProject.Buckets
            .SelectMany(bucket => bucket.Notes)
            .Where(note => note.IsImage && note.Image is not null))
        {
            var bytes = storage.ReadAsset(sourceProject, note.Image!.RelativePath);
            if (bytes is null)
            {
                continue;
            }

            var extension = Path.GetExtension(note.Image.RelativePath);
            note.Image.RelativePath = storage.WriteAsset(
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
            .Where(name => !IsDeletedBucketName(name))
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

    public static bool IsDeletedBucket(ZetlBucket bucket)
    {
        return string.Equals(bucket.Kind, DeletedBucketKind, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeletedBucketName(string? name)
    {
        return string.Equals(name?.Trim(), DeletedBucketName, StringComparison.OrdinalIgnoreCase);
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
        if (string.Equals(kind, DeletedBucketKind, StringComparison.OrdinalIgnoreCase))
        {
            return DeletedBucketKind;
        }

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
        fifoBucket.Revision++;
        return reviewBucket;
    }

    public static string PreviewText(string text)
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

    private static int BucketDepth(ZetlBucket bucket, IReadOnlyList<ZetlBucket> allBuckets)
    {
        var depth = 0;
        var parentId = bucket.ParentBucketId;
        while (parentId is not null && depth < allBuckets.Count)
        {
            depth++;
            parentId = allBuckets.FirstOrDefault(item => item.Id == parentId)
                ?.ParentBucketId;
        }

        return depth;
    }

    private static string IndentedLine(string text, int depth)
    {
        return $"{new string('\t', Math.Max(0, depth))}{text.Trim()}";
    }

    private static string IndentedText(string text, int depth)
    {
        var prefix = new string('\t', Math.Max(0, depth));
        return string.Join(
            Environment.NewLine,
            text.ReplaceLineEndings("\n")
                .Split('\n')
                .Select(line => $"{prefix}{line.TrimEnd()}"));
    }

    private static string NewId()
    {
        return Guid.NewGuid().ToString("N");
    }

    public static string DefaultProjectName(bool shifted = false)
    {
        var name = DateTime.Now.ToString("yyyy-MM-dd");
        return shifted ? $"{name} Shift" : name;
    }
}
