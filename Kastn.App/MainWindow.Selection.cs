using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    // The one explicit interpretation of the tree's selection, computed fresh from
    // the tree. SelectedSlips / TitleModeBucket / the batch logic all read this
    // rather than poking the tree independently.
    private KastnSelection CurrentSelection()
    {
        var nodes = projectTree.SelectedItems?.OfType<KastnTreeNode>().ToList()
            ?? (SelectedTreeNode is { } single ? [single] : []);
        return KastnSelection.Compute(nodes, SelectedTreeNode);
    }

    private KastnSelectionContext CaptureSelectionContext() => new(
        currentProject is null ? null : ProjectIndex,
        CurrentSelection(),
        editorState.SlipId,
        SelectedBucketId);

    private IReadOnlyList<ZetlSlipSnapshot> SelectedSlips() => CaptureSelectionContext().Slips;

    private ZetlSlipSnapshot? SelectedSlip => CaptureSelectionContext().DetailSlip;

    // A bucket title is a formatting target independent of any editor fallback.
    private ZetlBucketSnapshot? TitleModeBucket() => CaptureSelectionContext().TitleBucket;
}
