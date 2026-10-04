using System.Collections.ObjectModel;
using System.ComponentModel;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal enum KastnDateFilter
{
    All,
    Today,
    Last7Days,
    Last30Days
}

internal sealed record KastnBucketItem(
    string? Id,
    string Label,
    ZetlBucketSnapshot? Bucket);

internal enum KastnTreeNodeKind
{
    Bucket,
    Slip
}

/// <summary>
/// One node of the project tree: a bucket (with nested buckets and its slips as
/// children) or a slip leaf. A view-agnostic projection of the snapshot so the
/// tree's shape is unit-testable without the UI.
/// </summary>
internal sealed class KastnTreeNode : INotifyPropertyChanged
{
    public required KastnTreeNodeKind Kind { get; init; }
    public required string Id { get; init; }
    public required string Label { get; set; }
    public ZetlBucketSnapshot? Bucket { get; set; }
    public ZetlSlipSnapshot? Slip { get; set; }
    public bool IsBucket => Kind == KastnTreeNodeKind.Bucket;
    // A container bucket (group/table/latex) Kastn renders specially; a plain bucket is
    // an ordinary section. Both still count and toggle visibility as buckets.
    public string BucketRenderKind { get; init; } = "";
    public bool IsContainerBucket => IsBucket && CurrentBucketRenderKind.Length > 0;
    public bool IsPlainBucket => IsBucket && CurrentBucketRenderKind.Length == 0;
    public bool IsText => Kind == KastnTreeNodeKind.Slip && !IsPicture && !IsStructural;
    public bool IsPicture { get; init; }
    // A Kastn-only structural element (a divider, later group/table/latex): no document
    // icon, a named label instead of derived text.
    public bool IsStructural { get; set; }
    public bool IsExcluded { get; set; }
    public bool IsDeletedBucket { get; init; }

    // Dim hidden slips and fully hidden buckets so they read as present-but-inactive.
    public double NodeOpacity => IsVisibilityHidden ? 0.5 : 1.0;
    // Bucket nodes aggregate their complete subtree so one eye controls the same scope
    // represented by the badge.
    public int IncludedCount { get; set; }
    public int HiddenCount { get; set; }
    public int TotalSlipCount => IncludedCount + HiddenCount;
    public bool CanToggleVisibility => IsBucket ? TotalSlipCount > 0 : Slip is not null;
    public bool IsVisibilityHidden => IsBucket
        ? TotalSlipCount > 0 && IncludedCount == 0
        : IsExcluded;
    public bool IsVisibilityMixed => IsBucket && IncludedCount > 0 && HiddenCount > 0;
    public bool ShowOpenEye => !IsVisibilityHidden;
    public bool ShowClosedEye => IsVisibilityHidden;
    public string VisibilityToolTip => IsBucket
        ? IsVisibilityHidden ? "Show all slips in this bucket" : "Hide all slips in this bucket"
        : IsVisibilityHidden ? "Show in views and exports" : "Hide from views and exports";
    public string CountLabel => HiddenCount == 0
        ? IncludedCount.ToString()
        : $"{IncludedCount} · {HiddenCount} hidden";
    public ObservableCollection<KastnTreeNode> Children { get; init; } = [];

    // BucketRenderKind stays init-only for construction ergonomics; UpdateFrom
    // tracks the live value here so a render-kind change updates in place.
    private string? currentBucketRenderKind;
    private string CurrentBucketRenderKind => currentBucketRenderKind ?? BucketRenderKind;

    // Copy the fresh projection's state into this live node, notifying the
    // template-bound properties only when something displayed actually changed.
    // Reconciling in place (instead of resetting the TreeView's ItemsSource)
    // keeps unchanged rows' containers alive — a full reset re-realized every
    // row and cost about a second per refresh on a few-hundred-slip project.
    public void UpdateFrom(KastnTreeNode fresh)
    {
        // Always adopt the fresh snapshots: drag plans and undo recording read
        // current revisions from them.
        Bucket = fresh.Bucket;
        Slip = fresh.Slip;

        var freshRenderKind = fresh.CurrentBucketRenderKind;
        var displayChanged = !string.Equals(Label, fresh.Label, StringComparison.Ordinal)
            || !string.Equals(CurrentBucketRenderKind, freshRenderKind, StringComparison.Ordinal)
            || IsStructural != fresh.IsStructural
            || IsExcluded != fresh.IsExcluded
            || IncludedCount != fresh.IncludedCount
            || HiddenCount != fresh.HiddenCount;
        if (!displayChanged)
        {
            return;
        }

        Label = fresh.Label;
        currentBucketRenderKind = freshRenderKind;
        IsStructural = fresh.IsStructural;
        IsExcluded = fresh.IsExcluded;
        IncludedCount = fresh.IncludedCount;
        HiddenCount = fresh.HiddenCount;
        foreach (var property in DisplayProperties)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    private static readonly string[] DisplayProperties =
    [
        nameof(Label),
        nameof(IsContainerBucket),
        nameof(IsPlainBucket),
        nameof(IsText),
        nameof(IsStructural),
        nameof(NodeOpacity),
        nameof(TotalSlipCount),
        nameof(CanToggleVisibility),
        nameof(IsVisibilityMixed),
        nameof(ShowOpenEye),
        nameof(ShowClosedEye),
        nameof(VisibilityToolTip),
        nameof(CountLabel)
    ];

    // This node's slips: its own slip (if it is one) plus every slip beneath it.
    public IEnumerable<ZetlSlipSnapshot> TreeSlips()
    {
        if (Slip is { } slip)
        {
            yield return slip;
        }

        foreach (var child in Children)
        {
            foreach (var descendant in child.TreeSlips())
            {
                yield return descendant;
            }
        }
    }

    // Transient drag-and-drop feedback: true while this row is the live drop target, so
    // the template can draw a drop marker. Not part of the snapshot — set during a drag.
    private bool isDropTarget;
    public bool IsDropTarget
    {
        get => isDropTarget;
        set
        {
            if (isDropTarget == value)
            {
                return;
            }

            isDropTarget = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDropTarget)));
        }
    }

    // Transient insertion indicator during a bucket reorder drag: an accent line drawn at
    // the top (Before) or bottom (After) edge of this row, showing where a released bucket
    // will land. None hides both lines.
    private KastnDropEdge dropEdge;
    public KastnDropEdge DropEdge
    {
        get => dropEdge;
        set
        {
            if (dropEdge == value)
            {
                return;
            }

            dropEdge = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowDropBefore)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowDropAfter)));
        }
    }

    public bool ShowDropBefore => dropEdge == KastnDropEdge.Before;
    public bool ShowDropAfter => dropEdge == KastnDropEdge.After;

    public event PropertyChangedEventHandler? PropertyChanged;
}

// Where a reordered bucket will be inserted relative to a row.
internal enum KastnDropEdge
{
    None,
    Before,
    After
}

internal static class KastnWorkbench
{
    public static bool IsDeletedBucket(ZetlBucketSnapshot? bucket)
    {
        return string.Equals(bucket?.Settings.Kind, "Deleted", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<KastnBucketItem> BuildBucketHierarchy(
        ZetlProjectSnapshot project,
        bool includeAll = false)
        => BuildBucketHierarchy(new KastnProjectIndex(project), includeAll);

    public static IReadOnlyList<KastnBucketItem> BuildBucketHierarchy(
        KastnProjectIndex index,
        bool includeAll = false)
    {
        var project = index.Project;
        var result = new List<KastnBucketItem>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        if (includeAll)
        {
            result.Add(new KastnBucketItem(null, "All buckets", null));
        }

        AddChildren(parentId: null, depth: 0);

        foreach (var bucket in project.Buckets)
        {
            if (visited.Add(bucket.Id))
            {
                result.Add(new KastnBucketItem(
                    bucket.Id,
                    $"{new string(' ', ZetlTreeText.BucketDepth(bucket, project.Buckets) * 3)}{bucket.Name}",
                    bucket));
            }
        }

        return result;

        void AddChildren(string? parentId, int depth)
        {
            foreach (var child in index.Children(parentId)
                .OrderBy(bucket => bucket.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!visited.Add(child.Id))
                {
                    continue;
                }

                result.Add(new KastnBucketItem(
                    child.Id,
                    $"{new string(' ', depth * 3)}{child.Name}",
                    child));
                AddChildren(child.Id, depth + 1);
            }
        }
    }

    public static IReadOnlyList<KastnBucketItem> BuildBucketPickerChoices(
        ZetlProjectSnapshot project,
        bool includeAll = false)
        => BuildBucketPickerChoices(new KastnProjectIndex(project), includeAll);

    public static IReadOnlyList<KastnBucketItem> BuildBucketPickerChoices(
        KastnProjectIndex index,
        bool includeAll = false)
    {
        var result = new List<KastnBucketItem>();
        if (includeAll)
        {
            result.Add(new KastnBucketItem(null, "All buckets", null));
        }

        foreach (var item in BuildBucketHierarchy(index))
        {
            if (item.Bucket is null)
            {
                continue;
            }

            result.Add(item with { Label = index.BucketPathLabel(item.Bucket) });
        }

        return result;
    }

    /// <summary>
    /// Builds the bucket/slip tree for the project: top-level buckets (those with
    /// no parent), each carrying its sub-buckets first, then its own slips as leaf
    /// nodes in the order they appear in <paramref name="slips"/>. Pass all project
    /// slips for the full tree, or a filtered subset for a filtered tree. Slips
    /// whose bucket is absent are skipped. Excluded slips stay in the tree (flagged
    /// via <see cref="KastnTreeNode.IsExcluded"/>); they only drop out of rendered
    /// views.
    /// </summary>
    // deletedOnly=false (default) builds the normal working tree and excludes the
    // protected Deleted bucket, which Kastn surfaces behind a dedicated toggle
    // instead of letting it sort in among real buckets. deletedOnly=true builds the
    // Deleted-bucket-only tree for browsing and restoring soft-deleted slips.
    // Standalone projections use defaults; the window supplies its cached preference
    // through the indexed overload below.
    public static IReadOnlyList<KastnTreeNode> BuildProjectTree(
        ZetlProjectSnapshot project,
        IReadOnlyList<ZetlSlipSnapshot> slips,
        bool deletedOnly = false)
        => BuildProjectTree(new KastnProjectIndex(project), slips, deletedOnly,
            new ZetlAppSettings().MaxSlipLabelLength);

    public static IReadOnlyList<KastnTreeNode> BuildProjectTree(
        KastnProjectIndex index,
        IReadOnlyList<ZetlSlipSnapshot> slips,
        bool deletedOnly,
        int maxSlipLabelLength)
    {
        maxSlipLabelLength = Math.Max(2, maxSlipLabelLength);
        var slipsByBucket = ReferenceEquals(slips, index.Project.Slips) ? null : slips
            .GroupBy(slip => slip.BucketId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ZetlSlipSnapshot>)group.ToList(),
                StringComparer.Ordinal);

        return BuildLevel(parentId: null);

        IReadOnlyList<KastnTreeNode> BuildLevel(string? parentId)
        {
            var nodes = new List<KastnTreeNode>();
            // Buckets read in project.Buckets order — the canonical, manually
            // reorderable order — so the tree and board match the default view and
            // export instead of sorting alphabetically.
            foreach (var bucket in index.Children(parentId)
                .Where(bucket => deletedOnly == IsDeletedBucket(bucket)))
            {
                var bucketSlips = slipsByBucket is null
                    ? index.Slips(bucket.Id)
                    : slipsByBucket.TryGetValue(bucket.Id, out var found) ? found : [];
                var childNodes = BuildLevel(bucket.Id);
                var includedCount = bucketSlips.Count(slip => !slip.ExcludedFromViews)
                    + childNodes.Sum(child => child.IncludedCount);
                var hiddenCount = bucketSlips.Count(slip => slip.ExcludedFromViews)
                    + childNodes.Sum(child => child.HiddenCount);
                nodes.Add(new KastnTreeNode
                {
                    Kind = KastnTreeNodeKind.Bucket,
                    Id = bucket.Id,
                    Label = bucket.Name,
                    Bucket = bucket,
                    BucketRenderKind = ZetlViewRenderer.BucketRenderKind(bucket),
                    IsDeletedBucket = IsDeletedBucket(bucket),
                    IncludedCount = includedCount,
                    HiddenCount = hiddenCount,
                    Children = new ObservableCollection<KastnTreeNode>(
                        childNodes.Concat(bucketSlips.Select(slip => SlipNode(slip, maxSlipLabelLength))))
                });
            }

            return nodes;
        }
    }

    private static KastnTreeNode SlipNode(ZetlSlipSnapshot slip, int maxSlipLabelLength) => new()
    {
        Kind = KastnTreeNodeKind.Slip,
        Id = slip.Id,
        Label = SlipNodeLabel(slip, maxSlipLabelLength),
        Slip = slip,
        IsPicture = slip.Type == ZetlSlipType.Picture,
        IsStructural = ZetlBlockKinds.IsStructural(slip.BlockKind),
        IsExcluded = slip.ExcludedFromViews
    };

    private static string SlipNodeLabel(ZetlSlipSnapshot slip, int maxSlipLabelLength)
    {
        var label = SlipLabelText(slip);
        return label.Length <= maxSlipLabelLength
            ? label
            : label[..(maxSlipLabelLength - 1)].TrimEnd() + "…";
    }

    private static string SlipLabelText(ZetlSlipSnapshot slip)
    {
        // A structural slip has no authored content, so it reads by its kind. Divider is
        // the only structural slip kind (group/table/latex are container *buckets*).
        if (ZetlViewRenderer.SlipBlockKind(slip) == ZetlBlockKinds.Divider)
        {
            return "-- Divider";
        }

        if (!string.IsNullOrWhiteSpace(slip.Title))
        {
            return slip.Title.Trim();
        }

        if (!string.IsNullOrWhiteSpace(slip.Text))
        {
            var firstLine = slip.Text.Trim().Split('\n', 2)[0].Trim();
            return firstLine.Length > 0 ? firstLine : "(untitled)";
        }

        return slip.Type == ZetlSlipType.Picture ? "Picture" : "(untitled)";
    }

    public static string BucketPathLabel(
        ZetlProjectSnapshot project,
        ZetlBucketSnapshot bucket)
        => new KastnProjectIndex(project).BucketPathLabel(bucket);

    public static IReadOnlyList<ZetlSlipSnapshot> FilterSlips(
        ZetlProjectSnapshot project,
        string? bucketId,
        string? source,
        string? sessionId,
        KastnDateFilter dateFilter,
        string? search,
        DateTimeOffset now,
        ZetlSlipType? type = null)
        => FilterSlips(new KastnProjectIndex(project), bucketId, source, sessionId, dateFilter, search, now, type);

    public static IReadOnlyList<ZetlSlipSnapshot> FilterSlips(
        KastnProjectIndex index,
        string? bucketId,
        string? source,
        string? sessionId,
        KastnDateFilter dateFilter,
        string? search,
        DateTimeOffset now,
        ZetlSlipType? type = null)
    {
        var bucketIds = bucketId is null
            ? null
            : index.DescendantBucketIds(bucketId);
        var trimmedSearch = search?.Trim();
        var threshold = dateFilter switch
        {
            KastnDateFilter.Today => new DateTimeOffset(
                now.Year,
                now.Month,
                now.Day,
                0,
                0,
                0,
                now.Offset),
            KastnDateFilter.Last7Days => now.AddDays(-7),
            KastnDateFilter.Last30Days => now.AddDays(-30),
            _ => DateTimeOffset.MinValue
        };
        var query = index.Project.Slips.Where(slip =>
            (bucketIds is null || bucketIds.Contains(slip.BucketId))
            && (type is null || slip.Type == type)
            && (string.IsNullOrWhiteSpace(source)
                || string.Equals(slip.Source, source, StringComparison.Ordinal))
            && (string.IsNullOrWhiteSpace(sessionId)
                || string.Equals(slip.SessionId, sessionId, StringComparison.Ordinal))
            && slip.CapturedAtUtc >= threshold
            && (string.IsNullOrEmpty(trimmedSearch)
                || slip.Text.Contains(trimmedSearch, StringComparison.OrdinalIgnoreCase)
                || slip.Title.Contains(trimmedSearch, StringComparison.OrdinalIgnoreCase)));
        return query.ToList();
    }

}
