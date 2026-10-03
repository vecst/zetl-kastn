using ZETL;
using ZETL.Contracts;

namespace KASTN;

// Derived from one project snapshot and one selection. Capture again after a
// selection/snapshot change; do not retain this context across awaited commands.
internal sealed class KastnSelectionContext
{
    private readonly KastnProjectIndex? index;

    public KastnSelectionContext(
        KastnProjectIndex? index,
        KastnSelection selection,
        string? editorSlipId,
        string? primaryBucketId)
    {
        this.index = index;
        Selection = selection is KastnSelection.Slips slips
            ? new KastnSelection.Slips(slips.SlipIds.ToArray())
            : selection;
        EditorSlipId = editorSlipId;
        PrimaryBucket = index?.Bucket(primaryBucketId);
        TitleBucket = selection is KastnSelection.BucketTitle title
            && index?.Bucket(title.BucketId) is { } bucket
            && !KastnWorkbench.IsDeletedBucket(bucket)
                ? bucket : null;
        Slips = index is null ? [] : Selection switch
        {
            KastnSelection.Slips explicitSlips => index.SlipsInDocumentOrder(explicitSlips.SlipIds),
            KastnSelection.BucketTitle => [],
            _ => index.Slip(editorSlipId) is { } editorSlip ? [editorSlip] : []
        };
    }

    public KastnSelection Selection { get; }
    public string? EditorSlipId { get; }
    public bool HasProject => index is not null;
    public ZetlBucketSnapshot? PrimaryBucket { get; }
    public ZetlBucketSnapshot? TitleBucket { get; }
    public IReadOnlyList<ZetlSlipSnapshot> Slips { get; }
    public ZetlSlipSnapshot? SingleSlip => Slips.Count == 1 ? Slips[0] : null;

    // Preserve the detail pane's editor fallback independently of toolbar targets.
    public ZetlSlipSnapshot? DetailSlip => Slips.Count switch
    {
        0 => index?.Slip(EditorSlipId),
        1 => Slips[0],
        _ => null
    };

    public bool IsDeleted(ZetlSlipSnapshot slip) =>
        KastnWorkbench.IsDeletedBucket(index?.Bucket(slip.BucketId));
}

// Availability rules are independent of Avalonia. The window applies these
// values to controls and adds view-specific conditions such as move destinations.
internal sealed record KastnCommandAvailability
{
    public bool EditorEnabled { get; init; }
    public bool FormatSelectionEnabled { get; init; }
    public bool FormatBucketEnabled { get; init; }
    public bool AlignEnabled { get; init; }
    public bool CreateSlipEnabled { get; init; }
    public bool SlipPropertiesEnabled { get; init; }
    public bool MoveOrDeleteEnabled { get; init; }
    public bool RestoreSlipEnabled { get; init; }

    public static KastnCommandAvailability Compute(
        KastnSelectionContext context,
        bool online,
        bool busy,
        bool hasConflict,
        bool addingSlip)
    {
        var selected = context.Slips;
        var single = context.SingleSlip;
        var multiple = selected.Count > 1;
        var structural = single is not null && ZetlBlockKinds.IsStructural(single.BlockKind);
        var deleted = single is not null && context.IsDeleted(single);
        var canEdit = online && context.EditorSlipId is not null && !multiple && !structural && !busy;
        var canAct = online && !busy && !hasConflict;
        var editorEnabled = canEdit && !hasConflict;
        return new()
        {
            EditorEnabled = editorEnabled,
            FormatSelectionEnabled = editorEnabled || canAct && multiple
                && selected.Any(slip => slip.Type == ZetlSlipType.Text && !context.IsDeleted(slip)),
            FormatBucketEnabled = canAct && context.TitleBucket is not null,
            AlignEnabled = canAct && (context.TitleBucket is not null
                || selected.Any(slip => slip.Type == ZetlSlipType.Text
                    && !context.IsDeleted(slip) && !ZetlBlockKinds.IsStructural(slip.BlockKind))),
            CreateSlipEnabled = online && context.HasProject
                && !KastnWorkbench.IsDeletedBucket(context.PrimaryBucket) && !addingSlip,
            SlipPropertiesEnabled = editorEnabled && single is not null && !deleted,
            MoveOrDeleteEnabled = canAct && selected.Count > 0 && selected.All(slip => !context.IsDeleted(slip)),
            RestoreSlipEnabled = canEdit && deleted
        };
    }
}
