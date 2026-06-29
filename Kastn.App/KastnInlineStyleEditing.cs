using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal static class KastnInlineStyleEditing
{
    public static IReadOnlyList<ZetlInlineStyleRange> ToggleTextStyle(
        string text,
        IReadOnlyList<ZetlInlineStyleRange> inlineStyles,
        int start,
        int length,
        string kind)
    {
        kind = ZetlInlineStyleKinds.Normalize(kind);
        if (kind.Length == 0 || length <= 0)
        {
            return ZetlInlineStyles.Normalize(text, inlineStyles);
        }

        var normalized = ZetlInlineStyles.Normalize(text, inlineStyles);
        var selectionStart = Math.Clamp(start, 0, text.Length);
        var selectionEnd = Math.Clamp(start + length, selectionStart, text.Length);
        if (selectionEnd <= selectionStart)
        {
            return normalized;
        }

        var hasCoveringRange = normalized.Any(range =>
            string.Equals(range.Kind, kind, StringComparison.Ordinal)
            && range.Start <= selectionStart
            && range.Start + range.Length >= selectionEnd);
        var result = new List<ZetlInlineStyleRange>();
        foreach (var range in normalized)
        {
            if (!string.Equals(range.Kind, kind, StringComparison.Ordinal)
                || !Overlaps(range.Start, range.Start + range.Length, selectionStart, selectionEnd))
            {
                result.Add(range);
                continue;
            }

            result.AddRange(Subtract(range, selectionStart, selectionEnd));
        }

        if (!hasCoveringRange)
        {
            result.Add(new ZetlInlineStyleRange
            {
                Start = selectionStart,
                Length = selectionEnd - selectionStart,
                Kind = kind
            });
        }

        return ZetlInlineStyles.Normalize(text, result);
    }

    public static IReadOnlyList<ZetlInlineStyleRange> SetWebLink(
        string text,
        IReadOnlyList<ZetlInlineStyleRange> inlineStyles,
        int start,
        int length,
        string href) =>
        SetExclusiveRange(
            text,
            inlineStyles,
            start,
            length,
            new ZetlInlineStyleRange
            {
                Start = start,
                Length = length,
                Kind = ZetlInlineStyleKinds.Link,
                Href = href
            });

    public static IReadOnlyList<ZetlInlineStyleRange> SetWikiLink(
        string text,
        IReadOnlyList<ZetlInlineStyleRange> inlineStyles,
        int start,
        int length,
        string targetSlipId,
        string cachedTitle) =>
        SetExclusiveRange(
            text,
            inlineStyles,
            start,
            length,
            new ZetlInlineStyleRange
            {
                Start = start,
                Length = length,
                Kind = ZetlInlineStyleKinds.WikiLink,
                TargetSlipId = targetSlipId,
                CachedTitle = cachedTitle
            });

    public static IReadOnlyList<ZetlInlineStyleRange> ShiftForReplacement(
        string originalText,
        IReadOnlyList<ZetlInlineStyleRange> inlineStyles,
        int start,
        int replacedLength,
        int replacementLength)
    {
        var normalized = ZetlInlineStyles.Normalize(originalText, inlineStyles);
        if (normalized.Count == 0)
        {
            return [];
        }

        var replaceStart = Math.Clamp(start, 0, originalText.Length);
        var replaceEnd = Math.Clamp(replaceStart + replacedLength, replaceStart, originalText.Length);
        var delta = replacementLength - (replaceEnd - replaceStart);
        var result = new List<ZetlInlineStyleRange>();
        foreach (var range in normalized)
        {
            var rangeStart = range.Start;
            var rangeEnd = range.Start + range.Length;
            if (rangeEnd <= replaceStart)
            {
                result.Add(range);
            }
            else if (rangeStart >= replaceEnd)
            {
                result.Add(range with { Start = range.Start + delta });
            }
            else if (replacedLength == 0 && rangeStart < replaceStart && rangeEnd > replaceStart)
            {
                result.Add(range with { Length = range.Length + replacementLength });
            }
        }

        return result;
    }

    public static bool StyleListsEqual(
        IReadOnlyList<ZetlInlineStyleRange> left,
        IReadOnlyList<ZetlInlineStyleRange> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<ZetlInlineStyleRange> SetExclusiveRange(
        string text,
        IReadOnlyList<ZetlInlineStyleRange> inlineStyles,
        int start,
        int length,
        ZetlInlineStyleRange replacement)
    {
        if (length <= 0)
        {
            return ZetlInlineStyles.Normalize(text, inlineStyles);
        }

        var selectionStart = Math.Clamp(start, 0, text.Length);
        var selectionEnd = Math.Clamp(start + length, selectionStart, text.Length);
        var result = ZetlInlineStyles.Normalize(text, inlineStyles)
            .Where(range =>
                range.Kind is not (ZetlInlineStyleKinds.Link or ZetlInlineStyleKinds.WikiLink)
                || !Overlaps(range.Start, range.Start + range.Length, selectionStart, selectionEnd))
            .ToList();
        result.Add(replacement with
        {
            Start = selectionStart,
            Length = selectionEnd - selectionStart
        });
        return ZetlInlineStyles.Normalize(text, result);
    }

    private static IEnumerable<ZetlInlineStyleRange> Subtract(
        ZetlInlineStyleRange range,
        int start,
        int end)
    {
        var rangeStart = range.Start;
        var rangeEnd = range.Start + range.Length;
        if (rangeStart < start)
        {
            yield return range with { Length = start - rangeStart };
        }

        if (rangeEnd > end)
        {
            yield return range with { Start = end, Length = rangeEnd - end };
        }
    }

    private static bool Overlaps(int leftStart, int leftEnd, int rightStart, int rightEnd) =>
        leftStart < rightEnd && rightStart < leftEnd;
}
