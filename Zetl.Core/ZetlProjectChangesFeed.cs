using System.Text.Json;
using ZETL.Contracts;

namespace ZETL;

// Observed state versions, not a mutation/event log. Only lightweight revision
// stamps and order IDs are retained; intermediate transitions may be coalesced.
// Read() must run under the store's mutation lock, including reset snapshots.
internal sealed class ZetlProjectChangesFeed(int versionCapacity = 128, int perProjectCapacity = 32, int indexUnitCapacity = 200_000)
{
    private readonly record struct SlipStamp(long Revision, string BucketId);
    // Normalization can clear dangling parent/review references on an otherwise
    // untouched bucket without bumping its revision.
    private readonly record struct BucketStamp(long Revision, string? Parent, string? Review);
    private static BucketStamp Stamp(ZetlBucket bucket) => new(bucket.Revision, bucket.ParentBucketId, bucket.Settings.ReplayReviewBucketId);
    private sealed record Index(string Metadata, Dictionary<string, BucketStamp> Buckets,
        Dictionary<string, SlipStamp> Slips, string[] BucketOrder, Dictionary<string, string[]> SlipOrders)
    {
        public int Units => 2 * (Buckets.Count + Slips.Count);
    }
    private sealed record Entry(WeakReference<ZetlProject> Project, ZetlProjectCursor Cursor, Index? Index);
    private static bool IsProject(Entry entry, ZetlProject project) => entry.Project.TryGetTarget(out var target) && ReferenceEquals(target, project);
    private readonly string epoch = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, LinkedListNode<Entry>> versions = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> recency = new();
    private int units;
    internal int RetainedVersions => versions.Count;
    internal int RetainedIndexUnits => units;
    internal long IndexScans { get; private set; }
    internal long MappedSlips { get; private set; }

    public ZetlProjectChanges Read(ZetlProject project, ZetlProjectCursor? cursor)
    {
        Entry? previous = null;
        string? reset = cursor is null ? "bootstrap" : null;
        if (cursor is not null)
        {
            if (cursor.Epoch != epoch) reset = "epoch_changed";
            else if (cursor.ProjectId != project.Id || cursor.Sequence < 0 || string.IsNullOrEmpty(cursor.Token)) reset = "cursor_invalid";
            else if (!versions.TryGetValue(cursor.Token, out var node)) reset = "cursor_expired";
            else if (node.Value.Cursor != cursor) reset = "cursor_invalid";
            else if (!IsProject(node.Value, project) || cursor.Sequence > project.ChangeSequence) reset = "project_replaced";
            else { previous = node.Value; Touch(node); }
        }
        if (previous is not null && previous.Cursor.Sequence == project.ChangeSequence)
            return new() { ProjectId = project.Id, Cursor = previous.Cursor, FromCursor = cursor };
        var current = recency.FirstOrDefault(e => IsProject(e, project) && e.Cursor.Sequence == project.ChangeSequence);
        if (current is null)
        {
            var index = BuildIndex(project);
            current = new(new(project), new() { Epoch = epoch, ProjectId = project.Id, Token = Guid.NewGuid().ToString("N"), Sequence = project.ChangeSequence },
                index.Units <= Math.Max(0, indexUnitCapacity) ? index : null);
            Add(current);
        }
        else Touch(versions[current.Cursor.Token]);
        if (reset is not null || previous?.Index is null || current.Index is null)
        {
            var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(project);
            MappedSlips += snapshot.Slips.Count;
            return new() { ProjectId = project.Id, Cursor = current.Cursor, FromCursor = cursor,
                ResyncRequired = cursor is not null, ResetReason = reset ?? "capacity_limit", Snapshot = snapshot };
        }
        var before = previous.Index; var after = current.Index;
        var changedBuckets = project.Buckets.Where(b => !before.Buckets.TryGetValue(b.Id, out var stamp) || stamp != Stamp(b))
            .Select(ZetlProjectSnapshotMapper.ToSnapshot).ToArray();
        var changedSlips = project.Buckets.SelectMany(b => b.Slips.Where(s =>
                !before.Slips.TryGetValue(s.Id, out var stamp) || stamp != new SlipStamp(s.Revision, b.Id))
            .Select(s => ZetlProjectSnapshotMapper.ToSnapshot(b, s))).ToArray();
        MappedSlips += changedSlips.Length;
        return new() { ProjectId = project.Id, Cursor = current.Cursor, FromCursor = cursor,
            Metadata = before.Metadata == after.Metadata ? null : ZetlProjectSnapshotMapper.ToMetadataSnapshot(project),
            Buckets = changedBuckets, Slips = changedSlips,
            RemovedBucketIds = before.Buckets.Keys.Except(after.Buckets.Keys).ToArray(),
            RemovedSlipIds = before.Slips.Keys.Except(after.Slips.Keys).ToArray(),
            BucketOrder = before.BucketOrder.SequenceEqual(after.BucketOrder) ? null : after.BucketOrder,
            SlipOrders = after.SlipOrders.Where(p => !before.SlipOrders.TryGetValue(p.Key, out var ids) || !ids.SequenceEqual(p.Value))
                .Select(p => new ZetlBucketSlipOrder { BucketId = p.Key, SlipIds = p.Value }).ToArray() };
    }
    private Index BuildIndex(ZetlProject project)
    {
        IndexScans++;
        // Metadata is small and can change without MetadataRevision (e.g. active
        // bucket). It excludes ChangeSequence so a capture does not resend views.
        var metadata = ZetlProjectSnapshotMapper.ToMetadataSnapshot(project) with { ChangeSequence = 0 };
        var signature = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(metadata, ZetlProtocolJson.Options)));
        return new(signature,
            project.Buckets.ToDictionary(b => b.Id, Stamp, StringComparer.Ordinal),
            project.Buckets.SelectMany(b => b.Slips.Select(s => (s.Id, Stamp: new SlipStamp(s.Revision, b.Id))))
                .ToDictionary(p => p.Id, p => p.Stamp, StringComparer.Ordinal),
            project.Buckets.Select(b => b.Id).ToArray(),
            project.Buckets.ToDictionary(b => b.Id, b => b.Slips.Select(s => s.Id).ToArray(), StringComparer.Ordinal));
    }
    private void Add(Entry entry)
    {
        versions[entry.Cursor.Token] = recency.AddLast(entry); units += entry.Index?.Units ?? 0;
        while (versions.Count > Math.Max(1, versionCapacity) || units > Math.Max(0, indexUnitCapacity)) Remove(recency.First!);
        while (recency.Count(e => e.Cursor.ProjectId == entry.Cursor.ProjectId) > Math.Max(1, perProjectCapacity))
            Remove(recency.First(n => n.Cursor.ProjectId == entry.Cursor.ProjectId).Cursor.Token);
    }
    private void Touch(LinkedListNode<Entry> node) { recency.Remove(node); recency.AddLast(node); }
    private void Remove(string token) => Remove(versions[token]);
    private void Remove(LinkedListNode<Entry> node)
    { units -= node.Value.Index?.Units ?? 0; versions.Remove(node.Value.Cursor.Token); recency.Remove(node); }
}
