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
    public required string Label { get; init; }
    public ZetlBucketSnapshot? Bucket { get; init; }
    public ZetlSlipSnapshot? Slip { get; init; }
    public bool IsBucket => Kind == KastnTreeNodeKind.Bucket;
    // A container bucket (group/table/latex) Kastn renders specially; a plain bucket is
    // an ordinary section. Both still count and toggle visibility as buckets.
    public string BucketRenderKind { get; init; } = "";
    public bool IsContainerBucket => IsBucket && BucketRenderKind.Length > 0;
    public bool IsPlainBucket => IsBucket && BucketRenderKind.Length == 0;
    public bool IsText => Kind == KastnTreeNodeKind.Slip && !IsPicture && !IsStructural;
    public bool IsPicture { get; init; }
    // A Kastn-only structural element (a divider, later group/table/latex): no document
    // icon, a named label instead of derived text.
    public bool IsStructural { get; init; }
    public bool IsExcluded { get; init; }
    public bool IsDeletedBucket { get; init; }

    // Dim hidden slips and fully hidden buckets so they read as present-but-inactive.
    public double NodeOpacity => IsVisibilityHidden ? 0.5 : 1.0;
    // Bucket nodes aggregate their complete subtree so one eye controls the same scope
    // represented by the badge.
    public int IncludedCount { get; init; }
    public int HiddenCount { get; init; }
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
    public IReadOnlyList<KastnTreeNode> Children { get; init; } = [];

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

    public event PropertyChangedEventHandler? PropertyChanged;
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
    {
        var result = new List<KastnBucketItem>();
        if (includeAll)
        {
            result.Add(new KastnBucketItem(null, "All buckets", null));
        }

        AddChildren(parentId: null, depth: 0);

        foreach (var bucket in project.Buckets)
        {
            if (result.All(item => item.Id != bucket.Id))
            {
                result.Add(new KastnBucketItem(
                    bucket.Id,
                    $"{new string(' ', Depth(bucket, project.Buckets) * 3)}{bucket.Name}",
                    bucket));
            }
        }

        return result;

        void AddChildren(string? parentId, int depth)
        {
            foreach (var child in project.Buckets
                .Where(bucket => bucket.ParentBucketId == parentId)
                .OrderBy(bucket => bucket.Name, StringComparer.OrdinalIgnoreCase))
            {
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
    {
        var result = new List<KastnBucketItem>();
        if (includeAll)
        {
            result.Add(new KastnBucketItem(null, "All buckets", null));
        }

        foreach (var item in BuildBucketHierarchy(project))
        {
            if (item.Bucket is null)
            {
                continue;
            }

            result.Add(item with { Label = BucketPathLabel(project, item.Bucket) });
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
    public static IReadOnlyList<KastnTreeNode> BuildProjectTree(
        ZetlProjectSnapshot project,
        IReadOnlyList<ZetlSlipSnapshot> slips,
        bool deletedOnly = false)
    {
        var slipsByBucket = slips
            .GroupBy(slip => slip.BucketId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ZetlSlipSnapshot>)group.ToList(),
                StringComparer.Ordinal);

        return BuildLevel(parentId: null);

        IReadOnlyList<KastnTreeNode> BuildLevel(string? parentId)
        {
            var nodes = new List<KastnTreeNode>();
            foreach (var bucket in project.Buckets
                .Where(bucket => bucket.ParentBucketId == parentId)
                .Where(bucket => deletedOnly == IsDeletedBucket(bucket))
                .OrderBy(bucket => bucket.Name, StringComparer.OrdinalIgnoreCase))
            {
                var bucketSlips = slipsByBucket.TryGetValue(bucket.Id, out var found)
                    ? found
                    : [];
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
                    Children = childNodes
                        .Concat(bucketSlips.Select(SlipNode))
                        .ToList()
                });
            }

            return nodes;
        }
    }

    private static KastnTreeNode SlipNode(ZetlSlipSnapshot slip) => new()
    {
        Kind = KastnTreeNodeKind.Slip,
        Id = slip.Id,
        Label = SlipNodeLabel(slip),
        Slip = slip,
        IsPicture = slip.Type == ZetlSlipType.Picture,
        IsStructural = ZetlViewRenderer.IsStructuralKind(slip.BlockKind),
        IsExcluded = slip.ExcludedFromViews
    };

    // Keep slip leaves short so the tree stays scannable regardless of pane width.
    private static int MaxSlipLabelLength => new ZETL.ZetlAppSettingsStore().Settings.MaxSlipLabelLength;

    private static string SlipNodeLabel(ZetlSlipSnapshot slip)
    {
        var label = SlipLabelText(slip);
        return label.Length <= MaxSlipLabelLength
            ? label
            : label[..(MaxSlipLabelLength - 1)].TrimEnd() + "…";
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
    {
        var names = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        ZetlBucketSnapshot? current = bucket;
        while (current is not null && visited.Add(current.Id))
        {
            names.Push(current.Name);
            current = current.ParentBucketId is null
                ? null
                : project.Buckets.FirstOrDefault(item => item.Id == current.ParentBucketId);
        }

        return string.Join(" > ", names);
    }

    public static IReadOnlyList<ZetlSlipSnapshot> FilterSlips(
        ZetlProjectSnapshot project,
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
            : DescendantBucketIds(project, bucketId);
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
        var query = project.Slips.Where(slip =>
            (bucketIds is null || bucketIds.Contains(slip.BucketId))
            && (type is null || slip.Type == type)
            && (string.IsNullOrWhiteSpace(source)
                || string.Equals(slip.Source, source, StringComparison.Ordinal))
            && (string.IsNullOrWhiteSpace(sessionId)
                || string.Equals(slip.SessionId, sessionId, StringComparison.Ordinal))
            && slip.CapturedAtUtc >= threshold
            && (string.IsNullOrWhiteSpace(search)
                || slip.Text.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                || slip.Title.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)));
        return query.ToList();
    }

    public static string BuildViewerText(
        ZetlProjectSnapshot project,
        IReadOnlyList<ZetlSlipSnapshot> visibleSlips)
    {
        var parts = new List<string> { project.Name.Trim(), "" };
        var slipLookup = visibleSlips
            .GroupBy(slip => slip.BucketId)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (var item in BuildBucketHierarchy(project))
        {
            if (item.Bucket is null || !slipLookup.TryGetValue(item.Id!, out var bucketSlips))
            {
                continue;
            }

            var depth = Depth(item.Bucket, project.Buckets);
            parts.Add(IndentedText(item.Bucket.Name.Trim(), depth));
            parts.AddRange(bucketSlips
                .Select(slip => IndentedText(
                    (string.IsNullOrWhiteSpace(slip.Text) ? slip.Title : slip.Text).Trim(),
                    depth + 1))
                .Where(text => text.Trim().Length > 0));
            parts.Add("");
        }

        return string.Join(Environment.NewLine, parts).TrimEnd();
    }

    private static HashSet<string> DescendantBucketIds(
        ZetlProjectSnapshot project,
        string bucketId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { bucketId };
        var added = true;
        while (added)
        {
            added = false;
            foreach (var bucket in project.Buckets)
            {
                if (bucket.ParentBucketId is not null
                    && result.Contains(bucket.ParentBucketId)
                    && result.Add(bucket.Id))
                {
                    added = true;
                }
            }
        }

        return result;
    }

    private static int Depth(
        ZetlBucketSnapshot bucket,
        IReadOnlyList<ZetlBucketSnapshot> allBuckets)
    {
        var depth = 0;
        var parentId = bucket.ParentBucketId;
        while (parentId is not null && depth < allBuckets.Count)
        {
            depth++;
            parentId = allBuckets.FirstOrDefault(item => item.Id == parentId)
                ?.ParentBucketId;
        }

        return depth;
    }

    private static string IndentedText(string text, int depth)
    {
        var indent = new string('\t', Math.Max(0, depth));
        return string.Join(
            Environment.NewLine,
            text.ReplaceLineEndings("\n")
                .Split('\n')
                .Select(line => $"{indent}{line.TrimEnd()}"));
    }
}

internal sealed class KastnEditorState
{
    public string? SlipId { get; private set; }
    public long Revision { get; private set; }
    public string BaselineText { get; private set; } = "";
    public string DraftText { get; private set; } = "";
    public IReadOnlyList<ZetlInlineStyleRange> BaselineInlineStyles { get; private set; } = [];
    public IReadOnlyList<ZetlInlineStyleRange> DraftInlineStyles { get; private set; } = [];
    public ZetlSlipSnapshot? ConflictCurrent { get; private set; }
    public bool IsDirty => SlipId is not null
        && (!string.Equals(DraftText, BaselineText, StringComparison.Ordinal)
            || !KastnInlineStyleEditing.StyleListsEqual(DraftInlineStyles, BaselineInlineStyles));

    public bool InlineStylesAreDirty => SlipId is not null
        && !KastnInlineStyleEditing.StyleListsEqual(DraftInlineStyles, BaselineInlineStyles);

    public void Select(ZetlSlipSnapshot? slip)
    {
        SlipId = slip?.Id;
        Revision = slip?.Revision ?? 0;
        BaselineText = slip?.Text ?? "";
        DraftText = BaselineText;
        BaselineInlineStyles = CopyInlineStyles(slip?.InlineStyles);
        DraftInlineStyles = BaselineInlineStyles;
        ConflictCurrent = null;
    }

    public void SetDraft(string text)
    {
        DraftText = text;
    }

    public void SetInlineStyles(IReadOnlyList<ZetlInlineStyleRange> inlineStyles)
    {
        DraftInlineStyles = CopyInlineStyles(inlineStyles);
    }

    public void Reconcile(
        ZetlSlipSnapshot? current,
        string? pendingSaveText = null)
    {
        if (SlipId is null || current is null || current.Id != SlipId)
        {
            return;
        }

        if (current.Revision == Revision)
        {
            return;
        }

        if (pendingSaveText is not null
            && string.Equals(current.Text, pendingSaveText.Trim(), StringComparison.Ordinal))
        {
            Accept(current, keepDraft: false);
            return;
        }

        if (IsDirty)
        {
            ConflictCurrent = current;
            return;
        }

        Accept(current, keepDraft: false);
    }

    public void AcceptSaved(ZetlSlipSnapshot slip)
    {
        Accept(slip, keepDraft: false);
    }

    public void UseCurrent()
    {
        if (ConflictCurrent is { } current)
        {
            Accept(current, keepDraft: false);
        }
    }

    public void PrepareOverwrite()
    {
        if (ConflictCurrent is { } current)
        {
            Revision = current.Revision;
            BaselineText = current.Text;
            BaselineInlineStyles = CopyInlineStyles(current.InlineStyles);
            ConflictCurrent = null;
        }
    }

    private void Accept(ZetlSlipSnapshot slip, bool keepDraft)
    {
        SlipId = slip.Id;
        Revision = slip.Revision;
        BaselineText = slip.Text;
        if (!keepDraft)
        {
            DraftText = slip.Text;
            DraftInlineStyles = CopyInlineStyles(slip.InlineStyles);
        }

        BaselineInlineStyles = CopyInlineStyles(slip.InlineStyles);
        ConflictCurrent = null;
    }

    private static IReadOnlyList<ZetlInlineStyleRange> CopyInlineStyles(
        IReadOnlyList<ZetlInlineStyleRange>? inlineStyles) =>
        inlineStyles?.Select(style => style with { }).ToList() ?? [];
}
