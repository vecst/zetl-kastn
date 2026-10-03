using ZETL.Contracts;
using static ZETL.ZetlStateRules;

namespace ZETL;

// Buckets: adding, nesting, renaming, ordering, and settings, plus the protected
// Scratch and Deleted buckets.
internal sealed partial class ZetlStateStore
{
    // setActive controls whether the new/found bucket becomes the project's
    // active bucket. Compiling into a bucket passes false so it never reshuffles
    // the destination project's active bucket (which may not even be the project
    // you are working in).
    public ZetlBucket AddBucket(ZetlProject project, string name, string? parentBucketId = null, bool setActive = true)
    {
        lock (stateGate)
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
    }

    public ZetlBucket GetOrCreateBucket(ZetlProject project, string name, bool setActive = true)
    {
        lock (stateGate)
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
    }

    // Like GetOrCreateBucket but scoped to a parent: a name matches only among the
    // given parent's own children. This is what lets every journal day carry its own
    // "Capture" / "Quick Note" child without the seven of each colliding by name.
    public ZetlBucket GetOrCreateChildBucket(ZetlProject project, string? parentBucketId, string name, bool setActive = true)
    {
        lock (stateGate)
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
    }

    public void SetBucketHeading(ZetlBucket bucket, string align, bool bold, int level)
    {
        lock (stateGate)
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
    }

    public bool UpdateBucketName(ZetlBucket bucket, string name)
    {
        lock (stateGate)
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
    }

    public void DeleteBucket(ZetlProject project, string bucketId)
    {
        lock (stateGate)
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
    }

    public ZetlBucket GetQuickNoteBucket(ZetlProject project)
    {
        var bucket = project.Buckets.FirstOrDefault(bucket =>
            bucket.Id == project.QuickNoteBucketId && !IsDeletedBucket(bucket));
        return bucket ?? GetScratchBucket(project);
    }

    public void SetQuickNoteBucket(ZetlProject project, string bucketId)
    {
        lock (stateGate)
        {
            if (project.Buckets.Any(bucket => bucket.Id == bucketId && !IsDeletedBucket(bucket)))
            {
                project.QuickNoteBucketId = bucketId;
                PersistProject(project);
            }
        }
    }

    public void SetBucketKind(ZetlBucket bucket, string kind)
    {
        lock (stateGate)
        {
            if (IsDeletedBucket(bucket))
            {
                EnsureDeletedBucketShape(bucket);
                bucket.Revision++;
                PersistBucket(bucket);
                return;
            }

            bucket.Settings.Kind = NormalizeBucketKind(kind);

            bucket.Revision++;
            PersistBucket(bucket);
        }
    }

    public void UpdateBucketSettings(
        ZetlBucket bucket,
        string name,
        string defaultKind,
        string defaultCompileMode,
        string defaultStartingText,
        int defaultTsvRowLength)
    {
        lock (stateGate)
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
            }
            else
            {
                bucket.Settings.DefaultKind = NormalizeBucketKind(defaultKind);
                bucket.Settings.Kind = bucket.Settings.DefaultKind;
                bucket.Settings.DefaultCompileMode = NormalizeCompileMode(defaultCompileMode);
                bucket.Settings.DefaultStartingText = (defaultStartingText ?? "").Trim();
                bucket.Settings.DefaultTsvRowLength = Math.Max(1, defaultTsvRowLength);
            }

            bucket.Revision++;
            PersistBucket(bucket);
        }
    }

    // Creates a bucket carrying its complete definition in one durable write, so
    // the new bucket starts at revision 1 with everything the caller asked for.
    public ZetlBucket AddBucket(ZetlProject project, ZetlBucketDefinition definition)
    {
        lock (stateGate)
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
    }

    public void UpdateBucket(ZetlProject project, ZetlBucket bucket, ZetlBucketDefinition definition)
    {
        lock (stateGate)
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
        bucket.Settings.ReplayReviewBucketId = definition.ReplayReviewBucketId != bucket.Id
            && project.Buckets.Any(item => item.Id == definition.ReplayReviewBucketId && !IsDeletedBucket(item))
                ? definition.ReplayReviewBucketId
                : null;
        if (definition.RenderKind is not null)
        {
            bucket.RenderKind = ZetlBucketRenderKinds.Normalize(definition.RenderKind);
        }
    }

    // Reposition a bucket among its siblings (buckets sharing its parent) in the flat
    // project.Buckets list. Only sibling-relative order matters to the tree and
    // renderer — descendants are linked by ParentBucketId, not list adjacency — so the
    // single entry moves and its children come with it implicitly. A null anchor moves
    // the bucket to the end of its sibling group; otherwise it lands immediately before
    // the anchor, which must be a sibling.
    public bool ReorderBucket(ZetlProject project, ZetlBucket bucket, string? beforeBucketId)
    {
        lock (stateGate)
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
    }

    public ZetlBucket GetScratchBucket(ZetlProject project)
    {
        lock (stateGate)
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

    public ZetlBucket GetDeletedBucket(ZetlProject project)
    {
        lock (stateGate)
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
        bucket.Settings.ReplayReviewBucketId = null;
        bucket.Settings.PassThroughReviewBucketId = null;
    }

    // Stamp a freshly created bucket with the user's default compile mode and
    // TSV row length.
    private void ApplyBucketDefaults(ZetlBucket bucket)
    {
        bucket.Settings.DefaultCompileMode = NormalizeCompileMode(Defaults.CompileMode);
        bucket.Settings.DefaultTsvRowLength = Math.Max(1, Defaults.TsvRowLength);
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
}
