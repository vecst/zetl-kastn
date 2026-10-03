using ZETL.Contracts;
using ZETL;

namespace KASTN;

// Read-only queries over one snapshot. Replace the index with the snapshot;
// never update its dictionaries independently of the revisions they describe.
internal sealed class KastnProjectIndex
{
    private readonly Dictionary<string, ZetlBucketSnapshot> bucketsById;
    private readonly Dictionary<string, (ZetlSlipSnapshot Slip, int Position)> slipsById;
    private readonly ILookup<string, ZetlBucketSnapshot> childrenByParent;
    private readonly Dictionary<string, IReadOnlyList<ZetlSlipSnapshot>> slipsByBucket;
    private IReadOnlyDictionary<string, IReadOnlyList<ZetlSlipBacklink>>? backlinks;

    public KastnProjectIndex(ZetlProjectSnapshot project)
    {
        Project = project;
        bucketsById = project.Buckets.ToDictionary(bucket => bucket.Id, StringComparer.Ordinal);
        slipsById = project.Slips.Select((slip, position) => (Slip: slip, Position: position))
            .ToDictionary(entry => entry.Slip.Id, StringComparer.Ordinal);
        childrenByParent = project.Buckets.ToLookup(bucket => bucket.ParentBucketId ?? "", StringComparer.Ordinal);
        slipsByBucket = project.Slips.GroupBy(slip => slip.BucketId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ZetlSlipSnapshot>)group.ToList(), StringComparer.Ordinal);
    }

    public ZetlProjectSnapshot Project { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<ZetlSlipBacklink>> Backlinks =>
        backlinks ??= ZetlSlipLinks.BuildBacklinkIndex(Project);

    public ZetlBucketSnapshot? Bucket(string? id) =>
        id is not null && bucketsById.TryGetValue(id, out var bucket) ? bucket : null;

    public ZetlSlipSnapshot? Slip(string? id) =>
        id is not null && slipsById.TryGetValue(id, out var entry) ? entry.Slip : null;

    public IReadOnlyList<ZetlSlipSnapshot> SlipsInDocumentOrder(IEnumerable<string> ids)
    {
        var selected = new List<(ZetlSlipSnapshot Slip, int Position)>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (slipsById.TryGetValue(id, out var entry))
            {
                selected.Add(entry);
            }
        }
        selected.Sort((left, right) => left.Position.CompareTo(right.Position));
        return selected.Select(entry => entry.Slip).ToArray();
    }

    // Lookups retain snapshot order, including manually reordered siblings.
    public IEnumerable<ZetlBucketSnapshot> Children(string? parentId) => childrenByParent[parentId ?? ""];

    public IReadOnlyList<ZetlSlipSnapshot> Slips(string bucketId) =>
        slipsByBucket.TryGetValue(bucketId, out var slips) ? slips : [];

    public bool IsDescendant(string candidateId, string ancestorId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var current = Bucket(candidateId); current?.ParentBucketId is { } parentId
            && visited.Add(current.Id); current = Bucket(parentId))
        {
            if (parentId == ancestorId) return true;
        }
        return false;
    }

    public IReadOnlySet<string> DescendantBucketIds(string bucketId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(bucketId);
        while (pending.TryPop(out var id))
        {
            if (!result.Add(id))
            {
                continue;
            }

            foreach (var child in Children(id))
            {
                pending.Push(child.Id);
            }
        }

        return result;
    }

    public IEnumerable<ZetlBucketSnapshot> OrderedBuckets(bool deletedOnly = false)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bucket in Visit(null))
        {
            yield return bucket;
        }

        IEnumerable<ZetlBucketSnapshot> Visit(string? parentId)
        {
            foreach (var bucket in Children(parentId))
            {
                if (deletedOnly != KastnWorkbench.IsDeletedBucket(bucket) || !visited.Add(bucket.Id))
                {
                    continue;
                }

                yield return bucket;
                foreach (var child in Visit(bucket.Id))
                {
                    yield return child;
                }
            }
        }
    }

    public string BucketPathLabel(ZetlBucketSnapshot bucket)
    {
        var names = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (ZetlBucketSnapshot? current = bucket; current is not null && visited.Add(current.Id); current = Bucket(current.ParentBucketId))
        {
            names.Push(current.Name);
        }

        return string.Join(" > ", names);
    }
}
