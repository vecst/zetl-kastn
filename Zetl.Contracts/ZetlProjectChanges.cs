namespace ZETL.Contracts;

// An opaque, service-lifetime binding. The sequence alone is never a cursor:
// restored/replaced projects may reuse it, and retained versions can expire.
public sealed record ZetlProjectCursor
{
    public required string Epoch { get; init; }
    public required string ProjectId { get; init; }
    public required string Token { get; init; }
    public required long Sequence { get; init; }
}

public sealed record ZetlBucketSlipOrder
{
    public required string BucketId { get; init; }
    public IReadOnlyList<string> SlipIds { get; init; } = [];
}

public sealed record ZetlProjectChanges
{
    public required string ProjectId { get; init; }
    public required ZetlProjectCursor Cursor { get; init; }
    public ZetlProjectCursor? FromCursor { get; init; }
    public bool ResyncRequired { get; init; }
    // bootstrap, cursor_expired, epoch_changed, cursor_invalid, project_replaced,
    // or capacity_limit. A reset carries its snapshot and cursor atomically.
    public string? ResetReason { get; init; }
    public ZetlProjectSnapshot? Snapshot { get; init; }
    // All project fields including views; Buckets/Slips are always empty here.
    public ZetlProjectSnapshot? Metadata { get; init; }
    public IReadOnlyList<ZetlBucketSnapshot> Buckets { get; init; } = [];
    public IReadOnlyList<ZetlSlipSnapshot> Slips { get; init; } = [];
    public IReadOnlyList<string> RemovedBucketIds { get; init; } = [];
    public IReadOnlyList<string> RemovedSlipIds { get; init; } = [];
    public IReadOnlyList<string>? BucketOrder { get; init; }
    public IReadOnlyList<ZetlBucketSlipOrder> SlipOrders { get; init; } = [];
}

// Build and validate a candidate before publishing/persisting its cursor. This
// reducer preserves the full snapshot's bucket-major order and deleted slips.
public static class ZetlProjectChangesReducer
{
    public static ZetlProjectSnapshot Apply(ZetlProjectSnapshot? previous, ZetlProjectCursor? cursor, ZetlProjectChanges changes)
    {
        if (changes.Cursor.ProjectId != changes.ProjectId || changes.Cursor.Sequence < 0
            || string.IsNullOrWhiteSpace(changes.Cursor.Epoch) || string.IsNullOrWhiteSpace(changes.Cursor.Token))
            throw new InvalidDataException("Invalid project changes cursor.");
        if (changes.Snapshot is { } snapshot)
        {
            if (snapshot.Id != changes.ProjectId || snapshot.ChangeSequence != changes.Cursor.Sequence
                || changes.Metadata is not null || changes.Buckets.Count + changes.Slips.Count
                + changes.RemovedBucketIds.Count + changes.RemovedSlipIds.Count + changes.SlipOrders.Count != 0 || changes.BucketOrder is not null)
                throw new InvalidDataException("Invalid project changes reset snapshot.");
            Validate(snapshot);
            return snapshot;
        }
        if (changes.ResyncRequired || previous is null || cursor is null || changes.FromCursor != cursor
            || previous.Id != changes.ProjectId || previous.ChangeSequence != cursor.Sequence
            || changes.Cursor.Epoch != cursor.Epoch || changes.Cursor.Sequence < cursor.Sequence)
            throw new InvalidDataException("Project delta does not extend the applied cursor; resync required.");
        if (changes.Metadata is { } metadata && (metadata.Id != previous.Id || metadata.ChangeSequence != changes.Cursor.Sequence
            || metadata.Buckets.Count != 0 || metadata.Slips.Count != 0))
            throw new InvalidDataException("Invalid delta metadata.");
        if (changes.Metadata is null && changes.Buckets.Count + changes.Slips.Count + changes.RemovedBucketIds.Count
            + changes.RemovedSlipIds.Count + changes.SlipOrders.Count == 0 && changes.BucketOrder is null)
            return previous.ChangeSequence == changes.Cursor.Sequence ? previous : previous with { ChangeSequence = changes.Cursor.Sequence };
        var buckets = previous.Buckets.ToDictionary(b => b.Id, StringComparer.Ordinal);
        var slips = previous.Slips.ToDictionary(s => s.Id, StringComparer.Ordinal);
        RequireUnique(changes.Buckets.Select(b => b.Id)); RequireUnique(changes.Slips.Select(s => s.Id));
        RequireUnique(changes.RemovedBucketIds); RequireUnique(changes.RemovedSlipIds);
        if (changes.Buckets.Any(b => changes.RemovedBucketIds.Contains(b.Id)) || changes.Slips.Any(s => changes.RemovedSlipIds.Contains(s.Id)))
            throw new InvalidDataException("Delta both removes and replaces an entity.");
        foreach (var id in changes.RemovedBucketIds) buckets.Remove(id);
        foreach (var id in changes.RemovedSlipIds) slips.Remove(id);
        foreach (var bucket in changes.Buckets) buckets[bucket.Id] = bucket;
        foreach (var slip in changes.Slips) slips[slip.Id] = slip;
        var order = changes.BucketOrder ?? previous.Buckets.Select(b => b.Id).ToArray();
        RequireUnique(order);
        if (!buckets.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(order))
            throw new InvalidDataException("Delta bucket order is incomplete.");
        var slipOrders = previous.Slips.GroupBy(s => s.BucketId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<string>)g.Select(s => s.Id).ToArray(), StringComparer.Ordinal);
        foreach (var bucket in previous.Buckets) slipOrders.TryAdd(bucket.Id, []);
        RequireUnique(changes.SlipOrders.Select(o => o.BucketId));
        foreach (var updated in changes.SlipOrders)
        {
            if (!buckets.ContainsKey(updated.BucketId)) throw new InvalidDataException("Order targets a missing bucket.");
            slipOrders[updated.BucketId] = updated.SlipIds;
        }
        var ordered = new List<ZetlSlipSnapshot>(slips.Count);
        foreach (var bucketId in order)
        {
            if (!slipOrders.TryGetValue(bucketId, out var ids)) throw new InvalidDataException("New bucket lacks slip order.");
            RequireUnique(ids);
            foreach (var id in ids)
            {
                if (!slips.TryGetValue(id, out var slip) || slip.BucketId != bucketId)
                    throw new InvalidDataException("Slip order has a missing or misplaced slip.");
                ordered.Add(slip);
            }
        }
        if (ordered.Count != slips.Count) throw new InvalidDataException("Delta slip order is incomplete.");
        var result = (changes.Metadata ?? previous) with { ChangeSequence = changes.Cursor.Sequence,
            Buckets = order.Select(id => buckets[id]).ToArray(), Slips = ordered.ToArray() };
        Validate(result);
        return result;
    }
    private static void RequireUnique(IEnumerable<string> ids)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
            if (string.IsNullOrWhiteSpace(id) || !found.Add(id)) throw new InvalidDataException("Invalid or duplicate entity ID/order.");
    }
    private static void Validate(ZetlProjectSnapshot snapshot)
    {
        RequireUnique(snapshot.Buckets.Select(b => b.Id)); RequireUnique(snapshot.Slips.Select(s => s.Id));
        var buckets = snapshot.Buckets.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        if (snapshot.Slips.Any(s => !buckets.Contains(s.BucketId))) throw new InvalidDataException("Snapshot contains an orphan slip.");
    }
}
