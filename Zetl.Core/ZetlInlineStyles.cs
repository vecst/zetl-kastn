using ZETL.Contracts;

namespace ZETL;

internal static class ZetlInlineStyles
{
    public static List<ZetlInlineStyleRange> Normalize(
        string? text,
        IEnumerable<ZetlInlineStyleRange>? ranges)
    {
        var textLength = (text ?? "").Length;
        if (textLength == 0 || ranges is null)
        {
            return [];
        }

        var normalized = new List<ZetlInlineStyleRange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var range in ranges)
        {
            var kind = ZetlInlineStyleKinds.Normalize(range.Kind);
            if (kind.Length == 0 || range.Length <= 0)
            {
                continue;
            }

            var start = Math.Clamp(range.Start, 0, textLength);
            if (start >= textLength)
            {
                continue;
            }

            var length = Math.Min(range.Length, textLength - start);
            if (length <= 0)
            {
                continue;
            }

            var href = NormalizeOptional(range.Href);
            var targetSlipId = NormalizeOptional(range.TargetSlipId);
            var cachedTitle = NormalizeOptional(range.CachedTitle);
            if (kind == ZetlInlineStyleKinds.Link && href is null)
            {
                continue;
            }

            if (kind == ZetlInlineStyleKinds.WikiLink && targetSlipId is null)
            {
                continue;
            }

            if (kind is not ZetlInlineStyleKinds.Link and not ZetlInlineStyleKinds.WikiLink)
            {
                href = null;
                targetSlipId = null;
                cachedTitle = null;
            }

            if (kind == ZetlInlineStyleKinds.Link)
            {
                targetSlipId = null;
                cachedTitle = null;
            }

            var key = $"{start}\u001f{length}\u001f{kind}\u001f{href}\u001f{targetSlipId}\u001f{cachedTitle}";
            if (!seen.Add(key))
            {
                continue;
            }

            normalized.Add(new ZetlInlineStyleRange
            {
                Start = start,
                Length = length,
                Kind = kind,
                Href = href,
                TargetSlipId = targetSlipId,
                CachedTitle = cachedTitle
            });
        }

        return normalized
            .OrderBy(range => range.Start)
            .ThenBy(range => range.Length)
            .ThenBy(range => range.Kind, StringComparer.Ordinal)
            .ThenBy(range => range.Href, StringComparer.Ordinal)
            .ThenBy(range => range.TargetSlipId, StringComparer.Ordinal)
            .ThenBy(range => range.CachedTitle, StringComparer.Ordinal)
            .ToList();
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
