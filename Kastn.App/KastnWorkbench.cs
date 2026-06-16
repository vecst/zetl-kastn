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

internal static class KastnWorkbench
{
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

    public static IReadOnlyList<ZetlSlipSnapshot> FilterSlips(
        ZetlProjectSnapshot project,
        string? bucketId,
        string? source,
        string? sessionId,
        KastnDateFilter dateFilter,
        string? search,
        DateTimeOffset now)
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
            && (string.IsNullOrWhiteSpace(source)
                || string.Equals(slip.Source, source, StringComparison.Ordinal))
            && (string.IsNullOrWhiteSpace(sessionId)
                || string.Equals(slip.SessionId, sessionId, StringComparison.Ordinal))
            && slip.CapturedAtUtc >= threshold
            && (string.IsNullOrWhiteSpace(search)
                || slip.Text.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)));
        return query.ToList();
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
}

internal sealed class KastnEditorState
{
    public string? SlipId { get; private set; }
    public long Revision { get; private set; }
    public string BaselineText { get; private set; } = "";
    public string DraftText { get; private set; } = "";
    public ZetlSlipSnapshot? ConflictCurrent { get; private set; }
    public bool IsDirty => SlipId is not null
        && !string.Equals(DraftText, BaselineText, StringComparison.Ordinal);

    public void Select(ZetlSlipSnapshot? slip)
    {
        SlipId = slip?.Id;
        Revision = slip?.Revision ?? 0;
        BaselineText = slip?.Text ?? "";
        DraftText = BaselineText;
        ConflictCurrent = null;
    }

    public void SetDraft(string text)
    {
        DraftText = text;
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
        }

        ConflictCurrent = null;
    }
}
