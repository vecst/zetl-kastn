using System.Runtime.CompilerServices;
using ZETL.Contracts;
using static ZETL.ZetlStateRules;

namespace ZETL;

// Replay queues and pass-through: consuming items into review buckets, restoring
// them, and the temporary projects Replay runs from.
internal sealed partial class ZetlStateStore
{
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

    // A Replay bucket ran out. When it belongs to the consumable occupying this
    // lane, the lane goes back to the project active before the consumable
    // started (or to none), and a temporary consumable is deleted. Any other
    // Replay bucket just turns back into a Standard bucket.
    [MethodImpl(MethodImplOptions.Synchronized)]
    public ZetlReplayFinish FinishReplayBucket(ZetlBucket activeBucket, bool shifted)
    {
        var project = OwnerProject(activeBucket);
        var activeProjectId = shifted ? State.ShiftActiveProjectId : State.ActiveProjectId;
        var occupiesLane = project is not null
            && IsConsumableProject(project)
            && string.Equals(activeProjectId, project.Id, StringComparison.Ordinal);
        if (occupiesLane
            && IsTemporaryConsumableProject(project!)
            && string.Equals(project!.TemporaryLane, shifted ? ShiftLane : NormalLane, StringComparison.OrdinalIgnoreCase))
        {
            var returnedTo = ReturnFromConsumable(project, shifted);
            DisposeInactiveTemporaryProjects(persistWorkspace: true);
            return new ZetlReplayFinish(project.Name, Deleted: true, returnedTo);
        }

        SetBucketKind(activeBucket, "Standard");
        if (occupiesLane)
        {
            var returnedTo = ReturnFromConsumable(project!, shifted);
            PersistWorkspace();
            return new ZetlReplayFinish(activeBucket.Name, Deleted: false, returnedTo);
        }

        return new ZetlReplayFinish(activeBucket.Name, Deleted: false, ReturnedTo: null);
    }

    private string? ReturnFromConsumable(ZetlProject consumable, bool shifted)
    {
        var previous = State.Projects.FirstOrDefault(project =>
            project.Id == consumable.ReturnProjectId
            && project.Id != consumable.Id
            && IsActiveStatus(project));
        SetActiveProjectId(previous?.Id, shifted);
        return previous?.Name;
    }

    // Pass-through: a copy pasted straight away was only passing through, so
    // the latest automatic copy this session in the lane's capture project is
    // set aside in the project's Passed Through bucket when the paste matches
    // it (on its picture, else its exact text). Held captures and quick notes
    // never pass through, and older copies are never reached back for.
    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryPassThroughLatestCopy(
        string? text,
        string? imageSha256,
        bool shifted,
        out ZetlBucket? bucket,
        out ZetlSlip? slip,
        out ZetlBucket? reviewBucket,
        out ZetlSlip? reviewSlip)
    {
        bucket = null;
        slip = null;
        reviewBucket = null;
        reviewSlip = null;
        if (GetTapCaptureProject(shifted) is not { } project)
        {
            return false;
        }

        ZetlBucket? latestBucket = null;
        ZetlSlip? latest = null;
        foreach (var candidateBucket in project.Buckets)
        {
            if (IsReplayBucket(candidateBucket) || IsDeletedBucket(candidateBucket))
            {
                continue;
            }

            foreach (var candidate in candidateBucket.Slips)
            {
                if (IsAutoCopy(candidate)
                    && IsCurrentSessionSlip(candidate)
                    && !IsStructuralSlip(candidate)
                    && (latest is null || candidate.CreatedAtUtc >= latest.CreatedAtUtc))
                {
                    latest = candidate;
                    latestBucket = candidateBucket;
                }
            }
        }

        if (latest is null || latestBucket is null)
        {
            return false;
        }

        // A dual (text + picture) copy matches on its picture too.
        var matches =
            (imageSha256 is not null
                && latest.Image is not null
                && string.Equals(latest.Image.Sha256, imageSha256, StringComparison.OrdinalIgnoreCase))
            || (text is not null && string.Equals(latest.Text, text.Trim(), StringComparison.Ordinal));
        if (!matches)
        {
            return false;
        }

        bucket = latestBucket;
        return TryArchivePassedThroughSlip(latestBucket, latest, out slip, out reviewBucket, out reviewSlip);
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
    public void RestorePassedThroughSlip(ZetlBucket bucket, ZetlSlip note, ZetlBucket? reviewBucket, string? reviewNoteId)
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

    private bool TryArchivePassedThroughSlip(
        ZetlBucket sourceBucket,
        ZetlSlip sourceSlip,
        out ZetlSlip? passedSlip,
        out ZetlBucket? reviewBucket,
        out ZetlSlip? reviewSlip)
    {
        var project = OwnerProject(sourceBucket);
        if (project is null)
        {
            passedSlip = null;
            reviewBucket = null;
            reviewSlip = null;
            return false;
        }

        reviewBucket = GetOrCreatePassThroughReviewBucket(project, sourceBucket);
        sourceSlip.Revision++;
        sourceBucket.Slips.Remove(sourceSlip);
        passedSlip = sourceSlip;
        reviewSlip = CloneSlip(sourceSlip, "passed-through", CloneImageAssetReference(sourceSlip.Image));
        reviewBucket.Slips.Add(reviewSlip);
        PersistProject(project);
        return true;
    }

    // One Passed Through bucket per project, shared by all its buckets.
    private ZetlBucket GetOrCreatePassThroughReviewBucket(ZetlProject project, ZetlBucket sourceBucket)
    {
        var reviewBucket = GetOrCreateReviewBucket(
            project,
            sourceBucket,
            sourceBucket.Settings.PassThroughReviewBucketId,
            PassedThroughBucketName,
            PassedThroughBucketName);
        sourceBucket.Settings.PassThroughReviewBucketId = reviewBucket.Id;
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
        return reviewBucket;
    }
}
