using ZETL.Contracts;

namespace ZETL;

/// <summary>
/// The shared plain-text tree vocabulary: tab-indented bucket headings and slip
/// bodies at a bucket's nesting depth. Zetl's quick Compile, Kastn's viewer
/// text, and the Formatted/Plain views all speak this one format, so an indent
/// or depth change can never make them drift apart.
/// </summary>
internal static class ZetlTreeText
{
    public static string IndentedLine(string text, int depth) =>
        $"{new string('\t', Math.Max(0, depth))}{text.Trim()}";

    public static string IndentedText(string text, int depth)
    {
        var prefix = new string('\t', Math.Max(0, depth));
        return string.Join(
            Environment.NewLine,
            text.ReplaceLineEndings("\n")
                .Split('\n')
                .Select(line => $"{prefix}{line.TrimEnd()}"));
    }

    public static int BucketDepth(
        ZetlBucketSnapshot bucket,
        IReadOnlyList<ZetlBucketSnapshot> allBuckets) =>
        Depth(bucket.ParentBucketId, allBuckets.Count,
            id => allBuckets.FirstOrDefault(item => item.Id == id)?.ParentBucketId);

    public static int BucketDepth(
        ZetlBucketSnapshot bucket,
        Dictionary<string, ZetlBucketSnapshot> bucketsById) =>
        Depth(bucket.ParentBucketId, bucketsById.Count,
            id => bucketsById.TryGetValue(id, out var parent) ? parent.ParentBucketId : null);

    public static int BucketDepth(ZetlBucket bucket, IReadOnlyList<ZetlBucket> allBuckets) =>
        Depth(bucket.ParentBucketId, allBuckets.Count,
            id => allBuckets.FirstOrDefault(item => item.Id == id)?.ParentBucketId);

    // Walk to the root, capped at the bucket count so a corrupt parent cycle
    // terminates instead of hanging.
    private static int Depth(string? parentId, int bucketCount, Func<string, string?> parentOf)
    {
        var depth = 0;
        while (parentId is not null && depth < bucketCount)
        {
            depth++;
            parentId = parentOf(parentId);
        }

        return depth;
    }
}
