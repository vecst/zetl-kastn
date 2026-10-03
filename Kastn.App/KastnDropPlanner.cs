using ZETL.Contracts;

namespace KASTN;

internal enum KastnDropAction { SlipMove, BucketReparent, BucketReorder }
internal enum KastnDropTargetKind { Empty, Bucket, Slip, BoardSlot }
internal sealed record KastnDropPosition(KastnDropTargetKind Kind, string? TargetId = null,
    KastnDropEdge Edge = KastnDropEdge.None, string? BeforeSlipId = null, string? MarkerId = null);
internal sealed record KastnDropPlan(string ProjectId, KastnDropAction Action, string SourceId)
{
    public IReadOnlyList<string> SlipIds { get; init; } = [];
    public string? DestinationBucketId { get; init; }
    public string? BeforeSlipId { get; init; }
    public string? NewParentBucketId { get; init; }
    public string? BeforeBucketId { get; init; }
    public string? MarkerId { get; init; }
    public KastnDropEdge MarkerEdge { get; init; }
}

// Pure placement rules over one indexed snapshot. UI adapters resolve pointer
// positions and draw markers; plans contain only IDs and captured intent.
internal static class KastnDropPlanner
{
    public static KastnDropPlan? Plan(KastnProjectIndex index, string sourceId, bool sourceIsSlip,
        IEnumerable<string> draggedSlipIds, KastnDropPosition position)
    {
        if (sourceIsSlip)
        {
            if (index.Slip(sourceId) is not { } source || !IsActive(index, source.BucketId)) return null;
            var slips = index.SlipsInDocumentOrder(draggedSlipIds.Append(sourceId))
                .Where(slip => IsActive(index, slip.BucketId)).ToArray();
            var ids = slips.Select(slip => slip.Id).ToArray();
            var dragged = ids.ToHashSet(StringComparer.Ordinal);
            string? destination;
            string? before;
            if (position.Kind == KastnDropTargetKind.Slip)
            {
                if (index.Slip(position.TargetId) is not { } target || dragged.Contains(target.Id)) return null;
                destination = target.BucketId;
                before = position.Edge == KastnDropEdge.Before ? target.Id
                    : index.Slips(target.BucketId).SkipWhile(slip => slip.Id != target.Id)
                        .Skip(1).FirstOrDefault(slip => !dragged.Contains(slip.Id))?.Id;
            }
            else if (position.Kind is KastnDropTargetKind.Bucket or KastnDropTargetKind.BoardSlot)
            {
                destination = position.TargetId;
                before = position.Kind == KastnDropTargetKind.BoardSlot ? position.BeforeSlipId : null;
            }
            else return null;
            var plan = new KastnDropPlan(index.Project.Id, KastnDropAction.SlipMove, sourceId)
            {
                SlipIds = ids, DestinationBucketId = destination, BeforeSlipId = before,
                MarkerId = position.MarkerId ?? position.TargetId, MarkerEdge = position.Edge
            };
            return IsValid(index, plan) && !IsSlipNoOp(index, plan) ? plan : null;
        }

        if (index.Bucket(sourceId) is not { } moving || !IsActive(index, moving.Id)) return null;
        string? parent;
        string? anchor = null;
        var action = KastnDropAction.BucketReorder;
        var marker = position.TargetId;
        if (position.Kind == KastnDropTargetKind.Empty) parent = null;
        else
        {
            var target = position.Kind == KastnDropTargetKind.Slip
                ? index.Bucket(index.Slip(position.TargetId)?.BucketId) : index.Bucket(position.TargetId);
            if (target is null || !IsActive(index, target.Id)) return null;
            if (position.Kind == KastnDropTargetKind.Slip || position.Edge == KastnDropEdge.None)
            {
                parent = target.Id;
                marker = target.Id;
                action = KastnDropAction.BucketReparent;
            }
            else
            {
                parent = target.ParentBucketId;
                anchor = position.Edge == KastnDropEdge.Before ? target.Id : NextBucket(index, target);
            }
        }
        var bucketPlan = new KastnDropPlan(index.Project.Id, action, sourceId)
        {
            NewParentBucketId = parent, BeforeBucketId = anchor,
            MarkerId = marker, MarkerEdge = action == KastnDropAction.BucketReparent ? KastnDropEdge.None : position.Edge
        };
        if (!IsValid(index, bucketPlan)) return null;
        if (moving.ParentBucketId == parent
            && (action == KastnDropAction.BucketReparent || NextBucket(index, moving) == anchor)) return null;
        return bucketPlan;
    }

    // Revalidate captured IDs after autosave, and again between command awaits.
    public static bool IsValid(KastnProjectIndex index, KastnDropPlan plan)
    {
        if (index.Project.Id != plan.ProjectId) return false;
        if (plan.Action == KastnDropAction.SlipMove)
        {
            var dragged = plan.SlipIds.ToHashSet(StringComparer.Ordinal);
            return IsActive(index, plan.DestinationBucketId) && plan.SlipIds.Count > 0
                && dragged.Contains(plan.SourceId) && dragged.Count == plan.SlipIds.Count
                && plan.SlipIds.All(id => index.Slip(id) is { } slip && IsActive(index, slip.BucketId))
                && (plan.BeforeSlipId is null || !dragged.Contains(plan.BeforeSlipId)
                    && index.Slip(plan.BeforeSlipId)?.BucketId == plan.DestinationBucketId);
        }
        if (!IsActive(index, plan.SourceId) || !CanParent(index, plan.SourceId, plan.NewParentBucketId)) return false;
        return plan.BeforeBucketId is null || plan.BeforeBucketId != plan.SourceId
            && IsActive(index, plan.BeforeBucketId)
            && index.Bucket(plan.BeforeBucketId)?.ParentBucketId == plan.NewParentBucketId;
    }

    public static bool IsSlipNoOp(KastnProjectIndex index, KastnDropPlan plan)
    {
        var current = index.Slips(plan.DestinationBucketId!).Select(slip => slip.Id).ToArray();
        var dragged = plan.SlipIds.ToHashSet(StringComparer.Ordinal);
        if (plan.SlipIds.Any(id => index.Slip(id)?.BucketId != plan.DestinationBucketId)) return false;
        var desired = current.Where(id => !dragged.Contains(id)).ToList();
        var slot = plan.BeforeSlipId is null ? desired.Count : desired.IndexOf(plan.BeforeSlipId);
        if (slot < 0) return false;
        desired.InsertRange(slot, plan.SlipIds);
        return current.SequenceEqual(desired);
    }

    private static bool IsActive(KastnProjectIndex index, string? id) =>
        index.Bucket(id) is { } bucket && !KastnWorkbench.IsDeletedBucket(bucket);

    private static bool CanParent(KastnProjectIndex index, string sourceId, string? parentId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { sourceId };
        while (parentId is not null)
        {
            if (!visited.Add(parentId) || !IsActive(index, parentId)) return false;
            parentId = index.Bucket(parentId)!.ParentBucketId;
        }
        return true;
    }

    private static string? NextBucket(KastnProjectIndex index, ZetlBucketSnapshot bucket) =>
        index.Children(bucket.ParentBucketId).SkipWhile(item => item.Id != bucket.Id).Skip(1).FirstOrDefault()?.Id;
}
