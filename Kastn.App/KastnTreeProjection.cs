using System.Collections.ObjectModel;

namespace KASTN;

// Owns the live nodes independently of the window. Reconciliation preserves row
// identity, selection, expansion, and drag bindings while adopting fresh revisions.
internal sealed class KastnTreeProjection
{
    private readonly Dictionary<string, KastnTreeNode> nodesById = new(StringComparer.Ordinal);

    public ObservableCollection<KastnTreeNode> Roots { get; } = [];
    public KastnTreeNode? FirstSlip { get; private set; }

    public KastnTreeNode? Find(string? id) =>
        id is not null && nodesById.TryGetValue(id, out var node) ? node : null;

    public void Update(KastnProjectIndex project, bool deletedOnly, int maxSlipLabelLength)
    {
        ReconcileLevel(Roots, KastnWorkbench.BuildProjectTree(
            project, project.Project.Slips, deletedOnly, maxSlipLabelLength));
        nodesById.Clear();
        FirstSlip = null;
        IndexLevel(Roots);
    }

    public void Clear()
    {
        Roots.Clear();
        nodesById.Clear();
        FirstSlip = null;
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
