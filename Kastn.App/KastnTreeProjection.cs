using System.Collections.ObjectModel;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

// Owns the live nodes independently of the window. Reconciliation preserves row
// identity, selection, expansion, and drag bindings while adopting fresh revisions.
internal sealed class KastnTreeProjection
{
    private readonly Dictionary<string, KastnTreeNode> nodesById = new(StringComparer.Ordinal);
    private ZetlProjectSnapshot? lastProject;
    private bool lastDeletedOnly;
    private int lastLabelLength;

    public ObservableCollection<KastnTreeNode> Roots { get; } = [];
    public KastnTreeNode? FirstSlip { get; private set; }

    public KastnTreeNode? Find(string? id) =>
        id is not null && nodesById.TryGetValue(id, out var node) ? node : null;

    // Returns whether hierarchy/container membership needs reconciliation.
    public bool Update(KastnProjectIndex project, bool deletedOnly, int maxSlipLabelLength)
    {
        maxSlipLabelLength = Math.Max(2, maxSlipLabelLength);
        if (lastDeletedOnly == deletedOnly && HasSameStructure(project.Project))
        {
            UpdateContent(project, maxSlipLabelLength);
            lastProject = project.Project;
            lastLabelLength = maxSlipLabelLength;
            return false;
        }
        ReconcileLevel(Roots, KastnWorkbench.BuildProjectTree(
            project, project.Project.Slips, deletedOnly, maxSlipLabelLength));
        nodesById.Clear();
        FirstSlip = null;
        IndexLevel(Roots);
        lastProject = project.Project;
        lastDeletedOnly = deletedOnly;
        lastLabelLength = maxSlipLabelLength;
        return true;
    }

    public void Clear()
    {
        Roots.Clear();
        nodesById.Clear();
        FirstSlip = null;
        lastProject = null;
    }

    private bool HasSameStructure(ZetlProjectSnapshot project)
    {
        if (lastProject is not { } old || old.Id != project.Id
            || old.Buckets.Count != project.Buckets.Count || old.Slips.Count != project.Slips.Count) return false;
        for (var i = 0; i < project.Buckets.Count; i++)
        {
            var (before, after) = (old.Buckets[i], project.Buckets[i]);
            if (before.Id != after.Id || before.ParentBucketId != after.ParentBucketId
                || KastnWorkbench.IsDeletedBucket(before) != KastnWorkbench.IsDeletedBucket(after)) return false;
        }
        for (var i = 0; i < project.Slips.Count; i++)
        {
            var (before, after) = (old.Slips[i], project.Slips[i]);
            if (before.Id != after.Id || before.BucketId != after.BucketId
                || (before.Type == ZetlSlipType.Picture) != (after.Type == ZetlSlipType.Picture)) return false;
        }
        return true;
    }

    private void UpdateContent(KastnProjectIndex index, int labelLength)
    {
        var visibilityDeltas = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var slip in index.Project.Slips)
        {
            if (Find(slip.Id) is not { Slip: { } old } node) continue;
            if (old.ExcludedFromViews != slip.ExcludedFromViews)
            {
                var delta = slip.ExcludedFromViews ? 1 : -1;
                var visited = new HashSet<string>(StringComparer.Ordinal);
                for (var bucket = index.Bucket(slip.BucketId); bucket is not null && visited.Add(bucket.Id);
                    bucket = index.Bucket(bucket.ParentBucketId))
                    visibilityDeltas[bucket.Id] = visibilityDeltas.GetValueOrDefault(bucket.Id) + delta;
            }
            if (lastLabelLength != labelLength || old.Text != slip.Text || old.Title != slip.Title
                || old.BlockKind != slip.BlockKind || old.ExcludedFromViews != slip.ExcludedFromViews)
                node.UpdateFrom(KastnWorkbench.SlipNode(slip, labelLength));
            else node.Slip = slip; // Revisions and non-tree properties must also be current.
        }
        foreach (var bucket in index.Project.Buckets)
        {
            if (Find(bucket.Id) is not { IsBucket: true } node) continue;
            var delta = visibilityDeltas.GetValueOrDefault(bucket.Id);
            if (node.Bucket != bucket || delta != 0)
                node.UpdateFrom(new KastnTreeNode
                {
                    Kind = KastnTreeNodeKind.Bucket, Id = bucket.Id, Label = bucket.Name, Bucket = bucket,
                    BucketRenderKind = ZetlViewRenderer.BucketRenderKind(bucket),
                    IncludedCount = node.IncludedCount - delta, HiddenCount = node.HiddenCount + delta
                });
            else node.Bucket = bucket;
        }
    }

    private void IndexLevel(IEnumerable<KastnTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            nodesById.Add(node.Id, node);
            if (node.Kind == KastnTreeNodeKind.Slip)
            {
                FirstSlip ??= node;
            }
            IndexLevel(node.Children);
        }
    }

    private static void ReconcileLevel(ObservableCollection<KastnTreeNode> current, IReadOnlyList<KastnTreeNode> desired)
    {
        var desiredIds = desired.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = current.Count - 1; i >= 0; i--)
        {
            if (!desiredIds.Contains(current[i].Id))
            {
                current.RemoveAt(i);
            }
        }

        var existing = current.ToDictionary(node => node.Id, StringComparer.Ordinal);
        for (var i = 0; i < desired.Count; i++)
        {
            var want = desired[i];
            if (!existing.TryGetValue(want.Id, out var node))
            {
                current.Insert(i, want);
                continue;
            }

            if (!ReferenceEquals(current[i], node))
            {
                current.Move(current.IndexOf(node), i);
            }

            if (node.Kind != want.Kind || node.IsPicture != want.IsPicture)
            {
                current[i] = want;
            }
            else
            {
                node.UpdateFrom(want);
                ReconcileLevel(node.Children, want.Children);
            }
        }
    }
}
