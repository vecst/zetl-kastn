using ZETL.Contracts;
using ZETL;

namespace KASTN;

// Read-only queries over one snapshot. Successors may share immutable link state;
// captured indexes and their results continue to describe their original snapshot.
internal sealed class KastnProjectIndex
{
    private readonly Dictionary<string, ZetlBucketSnapshot> bucketsById;
    private readonly Dictionary<string, (ZetlSlipSnapshot Slip, int Position)> slipsById;
    private readonly ILookup<string, string> childrenByParent;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> slipIdsByBucket;
    private readonly Dictionary<string, IReadOnlyList<ZetlSlipSnapshot>> slipsByBucket = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<ZetlSlipBacklink>>? backlinks;
    private KastnBacklinkIndex? backlinkState;

    public KastnProjectIndex(ZetlProjectSnapshot project, KastnProjectIndex? previous = null)
    {
        Project = project;
        if (previous?.Project.Id != project.Id) previous = null;
        backlinkState = previous?.backlinkState;
        bucketsById = project.Buckets.ToDictionary(bucket => bucket.Id, StringComparer.Ordinal);
        slipsById = new(project.Slips.Count, StringComparer.Ordinal);
        var sameMembership = previous is not null && previous.Project.Slips.Count == project.Slips.Count;
        for (var i = 0; i < project.Slips.Count; i++)
        {
            var slip = project.Slips[i];
            slipsById.Add(slip.Id, (slip, i));
            sameMembership &= previous is not null && i < previous.Project.Slips.Count
                && previous.Project.Slips[i].Id == slip.Id && previous.Project.Slips[i].BucketId == slip.BucketId;
        }
        slipIdsByBucket = sameMembership ? previous!.slipIdsByBucket : project.Slips.GroupBy(slip => slip.BucketId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(slip => slip.Id).ToArray(), StringComparer.Ordinal);
        var sameParents = previous is not null && previous.Project.Buckets.Count == project.Buckets.Count;
        for (var i = 0; sameParents && i < project.Buckets.Count; i++)
            sameParents = previous!.Project.Buckets[i].Id == project.Buckets[i].Id
                && previous.Project.Buckets[i].ParentBucketId == project.Buckets[i].ParentBucketId;
        childrenByParent = sameParents ? previous!.childrenByParent
            : project.Buckets.ToLookup(bucket => bucket.ParentBucketId ?? "", bucket => bucket.Id, StringComparer.Ordinal);
    }

    public ZetlProjectSnapshot Project { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<ZetlSlipBacklink>> Backlinks
    {
        get
        {
            if (backlinks is null)
            {
                backlinkState = KastnBacklinkIndex.Update(Project, backlinkState, id => slipsById[id].Position);
                backlinks = backlinkState.Backlinks;
            }
            return backlinks;
        }
    }

    internal int ParsedBacklinkSourceCount => backlinks is null ? 0 : backlinkState!.ParsedSourceCount;

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
    public IEnumerable<ZetlBucketSnapshot> Children(string? parentId) =>
        childrenByParent[parentId ?? ""].Select(id => bucketsById[id]);

    public IReadOnlyList<ZetlSlipSnapshot> Slips(string bucketId)
    {
        if (!slipsByBucket.TryGetValue(bucketId, out var slips))
        {
            if (!slipIdsByBucket.TryGetValue(bucketId, out var ids)) return [];
            slipsByBucket[bucketId] = slips = ids.Select(id => slipsById[id].Slip).ToArray();
        }
        return slips;
    }

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
