using ZETL.Contracts;

namespace KASTN;

// The one explicit interpretation of what the project tree has selected. Everything
// the workbench derives from a selection — the editor binding, the toolbar target,
// the batch set, the right-pane mode — reads this single value instead of poking at
// the tree / hidden list / editor state independently.
//
// A Slips selection holds a set: one id is single-slip editing, two or more is a
// batch. A BucketTitle selection means a bucket is selected for editing its heading
// (not its slips). Compute is pure so it can be unit tested.
internal abstract record KastnSelection
{
    public sealed record None : KastnSelection;

    public sealed record Slips(IReadOnlyList<string> SlipIds) : KastnSelection;

    public sealed record BucketTitle(string BucketId) : KastnSelection;

    public static KastnSelection Compute(
        IReadOnlyList<KastnTreeNode> selectedNodes,
        KastnTreeNode? primaryNode)
    {
        // In multiple-selection TreeView mode Avalonia can leave older selected
        // rows in SelectedItems while SelectedItem has moved. A bucket primary row
        // is an explicit bucket-property edit, so it wins over stale slip rows.
        if (primaryNode is { Kind: KastnTreeNodeKind.Bucket } primaryBucket
            && !primaryBucket.IsDeletedBucket)
        {
            return new BucketTitle(primaryBucket.Id);
        }

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in selectedNodes)
        {
            // A bucket selection edits the bucket itself. Slip selections are explicit,
            // so bucket-level render defaults do not masquerade as batch slip edits.
            if (node.Kind == KastnTreeNodeKind.Bucket)
            {
                continue;
            }

            foreach (var slip in TreeSlips(node))
            {
                if (seen.Add(slip.Id))
                {
                    ids.Add(slip.Id);
                }
            }
        }

        if (ids.Count > 0)
        {
            return new Slips(ids);
        }

        return new None();
    }

    // A node's slips: its own slip (if it is one) plus every slip beneath it.
    private static IEnumerable<ZetlSlipSnapshot> TreeSlips(KastnTreeNode node)
    {
        if (node.Slip is { } slip)
        {
            yield return slip;
        }

        foreach (var child in node.Children)
        {
            foreach (var descendant in TreeSlips(child))
            {
                yield return descendant;
            }
        }
    }
}
