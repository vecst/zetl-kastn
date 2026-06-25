namespace ZETL;

internal static class LegacySlipTestExtensions
{
    public static IReadOnlyList<SlipDisplayItem> GetNoteDisplayItems(
        this ZetlStateStore store,
        ZetlProject project,
        IReadOnlyList<ZetlBucket>? bucketScope = null,
        bool currentSessionOnly = false) =>
        store.GetSlipDisplayItems(project, bucketScope, currentSessionOnly);

    public static bool TryGetLastNoteDisplayItem(
        this ZetlStateStore store,
        ZetlProject project,
        IReadOnlyList<ZetlBucket>? bucketScope,
        out SlipDisplayItem? slip,
        bool currentSessionOnly = false) =>
        store.TryGetLastSlipDisplayItem(project, bucketScope, out slip, currentSessionOnly);
}
