using System.Text.Json;
using ZETL.Contracts;

namespace KASTN;

// The state to restore one slip to: a snapshot to match, or Delete (the slip
// should not be present — soft-deleted). Soft-delete keeps the slip, so the
// opposite of a delete is always expressible as a restore to a snapshot.
internal sealed record KastnSlipMemento(ZetlSlipSnapshot? Restore)
{
    public bool IsDelete => Restore is null;

    public static readonly KastnSlipMemento Delete = new((ZetlSlipSnapshot?)null);

    public static KastnSlipMemento To(ZetlSlipSnapshot snapshot) => new(snapshot);
}

// One slip's part of a reversible edit: the slip is expected in state From (with
// its following neighbour FromFollowing) and should be returned to To (placed
// before ToFollowing). Undo and redo are symmetric — applying an operation yields
// its opposite by swapping From and To.
internal sealed record KastnUndoOperation(
    string SlipId,
    ZetlSlipSnapshot From,
    string? FromFollowing,
    KastnSlipMemento To,
    string? ToFollowing);

// One bucket's part of a reversible edit, mirroring the slip operation: the
// bucket is expected in state From (with its following same-parent sibling
// FromFollowing) and should be returned to To (placed before ToFollowing).
// Bucket undo covers rename/reparent/settings/heading/reorder; bucket creation
// and deletion are not recorded (their inverses need re-creation semantics).
internal sealed record KastnBucketUndoOperation(
    string BucketId,
    ZetlBucketSnapshot From,
    string? FromFollowing,
    ZetlBucketSnapshot To,
    string? ToFollowing);

// One reversible user action — a single edit or a whole gesture — as the set of
// per-slip and per-bucket operations needed to reverse it.
internal sealed record KastnUndoEntry(
    string Description,
    string ProjectId,
    IReadOnlyList<KastnUndoOperation> Operations)
{
    public string EntryId { get; init; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<KastnBucketUndoOperation> BucketOperations { get; init; } = [];
    public bool IsRepair { get; init; }
}

// One inverse command. BestEffort steps (position restore) may fail without
// failing the operation; a stale reorder anchor must not block an undo.
internal sealed record KastnInverseStep(ZetlCommandKind Kind, JsonElement Payload, bool BestEffort);

// Bounded, newest-on-top history of reversible actions. Used for both the undo
// and redo stacks. Project/server changes retire the captured run; a transient
// reconnect to the same server can retain its revision-checked entries.
internal sealed class KastnUndoHistory
{
    public const int DefaultCapacity = 100;

    private readonly List<KastnUndoEntry> entries = new();

    public KastnUndoHistory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count => entries.Count;

    public bool CanUndo => entries.Count > 0;

    public string? NextDescription => entries.Count > 0 ? entries[^1].Description : null;

    public void Push(KastnUndoEntry entry)
    {
        entries.Add(entry);
        if (entries.Count > Capacity)
        {
            entries.RemoveRange(0, entries.Count - Capacity);
        }
    }

    public bool TryPop(out KastnUndoEntry? entry)
    {
        if (entries.Count == 0)
        {
            entry = null;
            return false;
        }

        entry = entries[^1];
        entries.RemoveAt(entries.Count - 1);
        return true;
    }

    public bool TryPeek(out KastnUndoEntry? entry)
    {
        entry = entries.Count == 0 ? null : entries[^1];
        return entry is not null;
    }

    public bool TryPop(KastnUndoEntry expected)
    {
        if (entries.Count == 0
            || !string.Equals(entries[^1].EntryId, expected.EntryId, StringComparison.Ordinal))
        {
            return false;
        }

        entries.RemoveAt(entries.Count - 1);
        return true;
    }

    // Point every stored operation on the slip at its new current revision. Called
    // after the history itself changes a slip (an undo or redo step), so older
    // stacked entries for the same slip stay appliable instead of conflicting on
    // the revision the history's own step just advanced. A change made outside the
    // history still bumps the revision without re-threading and conflicts as before.
    public void RethreadRevision(string slipId, long revision)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Operations.All(op => !string.Equals(op.SlipId, slipId, StringComparison.Ordinal)))
            {
                continue;
            }

            entries[i] = entry with
            {
                Operations = entry.Operations
                    .Select(op => string.Equals(op.SlipId, slipId, StringComparison.Ordinal)
                        ? op with { From = op.From with { Revision = revision } }
                        : op)
                    .ToList()
            };
        }
    }

    // The bucket twin of RethreadRevision.
    public void RethreadBucketRevision(string bucketId, long revision)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.BucketOperations.All(op => !string.Equals(op.BucketId, bucketId, StringComparison.Ordinal)))
            {
                continue;
            }

            entries[i] = entry with
            {
                BucketOperations = entry.BucketOperations
                    .Select(op => string.Equals(op.BucketId, bucketId, StringComparison.Ordinal)
                        ? op with { From = op.From with { Revision = revision } }
                        : op)
                    .ToList()
            };
        }
    }

    public void Clear() => entries.Clear();
}

// Pure construction of the inverse commands for an operation, and of an
// operation's opposite. Snapshot-based so it can be unit-tested without a live
// connection.
internal static class KastnUndoPlanner
{
    // The commands that move a slip from its From state to its To state, expecting
    // From's revision on the first step. An empty result means the operation is a
    // no-op and should not be recorded.
    public static IReadOnlyList<KastnInverseStep> BuildSteps(KastnUndoOperation op)
    {
        if (op.To.IsDelete)
        {
            return [Step(ZetlCommandKind.DeleteSlip, new DeleteSlipCommand(), bestEffort: false)];
        }

        var target = op.To.Restore!;
        var steps = new List<KastnInverseStep>();

        var moved = !string.Equals(op.From.BucketId, target.BucketId, StringComparison.Ordinal);
        if (moved)
        {
            steps.Add(Step(
                ZetlCommandKind.MoveSlip,
                new MoveSlipCommand { DestinationBucketId = target.BucketId },
                bestEffort: false));
        }

        if (PropsDiffer(op.From, target))
        {
            steps.Add(Step(ZetlCommandKind.UpdateSlip, RestoreCommand(target), bestEffort: false));
        }

        if (moved || !string.Equals(op.FromFollowing, op.ToFollowing, StringComparison.Ordinal))
        {
            steps.Add(Step(
                ZetlCommandKind.ReorderSlip,
                new ReorderSlipCommand { BeforeSlipId = op.ToFollowing },
                bestEffort: true));
        }

        return steps;
    }

    // The operation that reverses op once it has been applied, given the slip's
    // snapshot afterwards. Swaps From and To so undo and redo share one executor.
    public static KastnUndoOperation Opposite(KastnUndoOperation op, ZetlSlipSnapshot now) =>
        new(op.SlipId, From: now, FromFollowing: op.ToFollowing, To: KastnSlipMemento.To(op.From), ToFollowing: op.FromFollowing);

    // True when the operation changes nothing, so it need not be recorded.
    public static bool IsNoOp(KastnUndoOperation op) => BuildSteps(op).Count == 0;

    // The commands that return a bucket from its From state to its To state:
    // identity/settings via UpdateBucket, heading styling via SetBucketHeading,
    // and a best-effort ReorderBucket when its sibling position (or parent)
    // changed. An empty result means the operation is a no-op.
    public static IReadOnlyList<KastnInverseStep> BuildBucketSteps(KastnBucketUndoOperation op)
    {
        var target = op.To;
        var steps = new List<KastnInverseStep>();

        var reparented = !string.Equals(op.From.ParentBucketId, target.ParentBucketId, StringComparison.Ordinal);
        if (reparented
            || !string.Equals(op.From.Name, target.Name, StringComparison.Ordinal)
            || op.From.Settings != target.Settings
            || !string.Equals(op.From.RenderKind, target.RenderKind, StringComparison.Ordinal))
        {
            steps.Add(Step(
                ZetlCommandKind.UpdateBucket,
                new UpdateBucketCommand
                {
                    Name = target.Name,
                    ParentBucketId = target.ParentBucketId,
                    Settings = target.Settings,
                    RenderKind = target.RenderKind
                },
                bestEffort: false));
        }

        if (!string.Equals(op.From.HeadingAlign, target.HeadingAlign, StringComparison.Ordinal)
            || op.From.HeadingBold != target.HeadingBold
            || op.From.HeadingLevel != target.HeadingLevel)
        {
            steps.Add(Step(
                ZetlCommandKind.SetBucketHeading,
                new SetBucketHeadingCommand
                {
                    Align = target.HeadingAlign,
                    Bold = target.HeadingBold,
                    Level = target.HeadingLevel
                },
                bestEffort: false));
        }

        if (reparented || !string.Equals(op.FromFollowing, op.ToFollowing, StringComparison.Ordinal))
        {
            steps.Add(Step(
                ZetlCommandKind.ReorderBucket,
                new ReorderBucketCommand { BeforeBucketId = op.ToFollowing },
                bestEffort: true));
        }

        return steps;
    }

    public static KastnBucketUndoOperation Opposite(KastnBucketUndoOperation op, ZetlBucketSnapshot now) =>
        new(op.BucketId, From: now, FromFollowing: op.ToFollowing, To: op.From, ToFollowing: op.FromFollowing);

    public static bool IsNoOp(KastnBucketUndoOperation op) => BuildBucketSteps(op).Count == 0;

    // The next bucket under the same parent in project order, or null when the
    // bucket is last among its siblings (or unknown).
    public static string? FollowingBucketId(ZetlProjectSnapshot project, string bucketId)
    {
        var target = project.Buckets.FirstOrDefault(bucket =>
            string.Equals(bucket.Id, bucketId, StringComparison.Ordinal));
        if (target is null)
        {
            return null;
        }

        var seenTarget = false;
        foreach (var bucket in project.Buckets)
        {
            if (!string.Equals(bucket.ParentBucketId, target.ParentBucketId, StringComparison.Ordinal))
            {
                continue;
            }

            if (seenTarget)
            {
                return bucket.Id;
            }

            if (string.Equals(bucket.Id, bucketId, StringComparison.Ordinal))
            {
                seenTarget = true;
            }
        }

        return null;
    }

    // Reconcile an interrupted compound history step against an authoritative
    // snapshot. The repair returns every touched record to its state before the
    // attempted step, including a command whose response was lost after Zetl may
    // already have committed it.
    public static KastnUndoEntry? BuildRepairEntry(
        KastnUndoEntry interrupted,
        ZetlProjectSnapshot project,
        string verb)
    {
        var slipRepairs = new List<KastnUndoOperation>();
        foreach (var original in interrupted.Operations)
        {
            var current = project.Slips.FirstOrDefault(slip =>
                string.Equals(slip.Id, original.SlipId, StringComparison.Ordinal));
            if (current is null)
            {
                continue;
            }

            var repair = new KastnUndoOperation(
                original.SlipId,
                current,
                FollowingSlipId(project, current.BucketId, current.Id),
                KastnSlipMemento.To(original.From),
                original.FromFollowing);
            if (!IsNoOp(repair))
            {
                slipRepairs.Add(repair);
            }
        }

        var bucketRepairs = new List<KastnBucketUndoOperation>();
        foreach (var original in interrupted.BucketOperations)
        {
            var current = project.Buckets.FirstOrDefault(bucket =>
                string.Equals(bucket.Id, original.BucketId, StringComparison.Ordinal));
            if (current is null)
            {
                continue;
            }

            var repair = new KastnBucketUndoOperation(
                original.BucketId,
                current,
                FollowingBucketId(project, current.Id),
                original.From,
                original.FromFollowing);
            if (!IsNoOp(repair))
            {
                bucketRepairs.Add(repair);
            }
        }

        slipRepairs.Reverse();
        bucketRepairs.Reverse();
        return slipRepairs.Count == 0 && bucketRepairs.Count == 0
            ? null
            : new KastnUndoEntry(
                $"Repair interrupted {verb}: {interrupted.Description}",
                interrupted.ProjectId,
                slipRepairs)
            {
                BucketOperations = bucketRepairs,
                IsRepair = true
            };
    }

    private static bool PropsDiffer(ZetlSlipSnapshot a, ZetlSlipSnapshot b) =>
        a.Type != b.Type
        || !string.Equals(a.Title, b.Title, StringComparison.Ordinal)
        || !string.Equals(a.Text, b.Text, StringComparison.Ordinal)
        || !string.Equals(Align(a), Align(b), StringComparison.Ordinal)
        || !string.Equals(a.BlockKind, b.BlockKind, StringComparison.Ordinal)
        || a.ExcludedFromViews != b.ExcludedFromViews
        || a.IgnoreBucketRenderKind != b.IgnoreBucketRenderKind
        || a.Checked != b.Checked
        || a.Bold != b.Bold
        || a.Italic != b.Italic
        || a.Strike != b.Strike
        || !string.Equals(a.FontFamily, b.FontFamily, StringComparison.Ordinal)
        || a.FontSize != b.FontSize
        || !string.Equals(a.TextColor, b.TextColor, StringComparison.Ordinal)
        || !InlineStylesEqual(a.InlineStyles, b.InlineStyles);

    private static string Align(ZetlSlipSnapshot slip) =>
        string.IsNullOrEmpty(slip.Align) ? "left" : slip.Align;

    private static bool InlineStylesEqual(
        IReadOnlyList<ZetlInlineStyleRange> a,
        IReadOnlyList<ZetlInlineStyleRange> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    // A full property restore, so the inverse returns the slip to its target state
    // regardless of which field changed.
    private static UpdateSlipCommand RestoreCommand(ZetlSlipSnapshot slip) => new()
    {
        Title = slip.Title,
        Text = slip.Text,
        Type = slip.Type,
        ExcludedFromViews = slip.ExcludedFromViews,
        Align = slip.Align ?? "left",
        BlockKind = slip.BlockKind,
        IgnoreBucketRenderKind = slip.IgnoreBucketRenderKind,
        Checked = slip.Checked,
        Bold = slip.Bold,
        Italic = slip.Italic,
        Strike = slip.Strike,
        FontFamily = slip.FontFamily,
        FontSize = slip.FontSize,
        TextColor = slip.TextColor,
        InlineStyles = slip.InlineStyles
    };

    private static KastnInverseStep Step<TPayload>(ZetlCommandKind kind, TPayload payload, bool bestEffort) =>
        new(kind, ZetlProtocolJson.ToElement(payload), bestEffort);

    // The slip immediately after the given slip within the same bucket, in project
    // order, or null when it is the last in its bucket.
    public static string? FollowingSlipId(ZetlProjectSnapshot project, string bucketId, string slipId)
    {
        var seenTarget = false;
        foreach (var slip in project.Slips)
        {
            if (!string.Equals(slip.BucketId, bucketId, StringComparison.Ordinal))
            {
                continue;
            }

            if (seenTarget)
            {
                return slip.Id;
            }

            if (string.Equals(slip.Id, slipId, StringComparison.Ordinal))
            {
                seenTarget = true;
            }
        }

        return null;
    }
}
