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
        if (!hasCoveringRange
            && kind == ZetlInlineStyleKinds.Code
            && normalized.Any(range =>
                range.Kind is ZetlInlineStyleKinds.Link or ZetlInlineStyleKinds.WikiLink
                && Overlaps(range.Start, range.Start + range.Length, selectionStart, selectionEnd)))
        {
            return normalized;
        }

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

    public static IReadOnlyList<ZetlInlineStyleRange> ReconcileTextEdit(
        string oldText,
        string newText,
        IReadOnlyList<ZetlInlineStyleRange> inlineStyles)
    {
        oldText ??= "";
        newText ??= "";
        var normalized = ZetlInlineStyles.Normalize(oldText, inlineStyles);
        if (normalized.Count == 0)
        {
            return [];
        }

        var prefix = CommonPrefixLength(oldText, newText);
        var oldSuffix = oldText.Length;
        var newSuffix = newText.Length;
        while (oldSuffix > prefix
            && newSuffix > prefix
            && oldText[oldSuffix - 1] == newText[newSuffix - 1])
        {
            oldSuffix--;
            newSuffix--;
        }

        var replacedLength = oldSuffix - prefix;
        var replacementLength = newSuffix - prefix;
        if (replacedLength == 0 && replacementLength == 0)
        {
            return ZetlInlineStyles.Normalize(newText, normalized);
        }

        return ReconcileSingleReplacement(
            newText,
            normalized,
            prefix,
            replacedLength,
            replacementLength);
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

    public static bool IsStyleActiveAtSelection(
        string text,
        IReadOnlyList<ZetlInlineStyleRange> inlineStyles,
        int start,
        int length,
        string kind)
    {
        kind = ZetlInlineStyleKinds.Normalize(kind);
        if (kind.Length == 0)
        {
            return false;
        }

        var textLength = (text ?? "").Length;
        var selectionStart = Math.Clamp(start, 0, textLength);
        var selectionEnd = Math.Clamp(start + Math.Max(0, length), selectionStart, textLength);
        return ZetlInlineStyles.Normalize(text, inlineStyles).Any(range =>
            string.Equals(range.Kind, kind, StringComparison.Ordinal)
            && CoversSelection(range, selectionStart, selectionEnd));
    }

    public static ZetlInlineStyleRange? CoveringRangeAtSelection(
        string text,
        IReadOnlyList<ZetlInlineStyleRange> inlineStyles,
        int start,
        int length,
        string kind)
    {
        kind = ZetlInlineStyleKinds.Normalize(kind);
        if (kind.Length == 0)
        {
            return null;
        }

        var textLength = (text ?? "").Length;
        var selectionStart = Math.Clamp(start, 0, textLength);
        var selectionEnd = Math.Clamp(start + Math.Max(0, length), selectionStart, textLength);
        return ZetlInlineStyles.Normalize(text, inlineStyles).FirstOrDefault(range =>
            string.Equals(range.Kind, kind, StringComparison.Ordinal)
            && CoversSelection(range, selectionStart, selectionEnd));
    }

    public static IReadOnlyList<ZetlInlineStyleRange> RemoveRange(
        string text,
        IReadOnlyList<ZetlInlineStyleRange> inlineStyles,
        ZetlInlineStyleRange remove)
    {
        var result = ZetlInlineStyles.Normalize(text, inlineStyles)
            .Where(range => !SameRange(range, remove))
            .ToList();
        return ZetlInlineStyles.Normalize(text, result);
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
        var normalized = ZetlInlineStyles.Normalize(text, inlineStyles);
        if (replacement.Kind is ZetlInlineStyleKinds.Link or ZetlInlineStyleKinds.WikiLink
            && normalized.Any(range =>
                range.Kind == ZetlInlineStyleKinds.Code
                && Overlaps(range.Start, range.Start + range.Length, selectionStart, selectionEnd)))
        {
            return normalized;
        }

        var result = normalized
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

    private static bool CoversSelection(ZetlInlineStyleRange range, int selectionStart, int selectionEnd)
    {
        var rangeEnd = range.Start + range.Length;
        return selectionEnd > selectionStart
            ? range.Start <= selectionStart && rangeEnd >= selectionEnd
            : range.Start <= selectionStart && selectionStart < rangeEnd;
    }

    private static bool SameRange(ZetlInlineStyleRange left, ZetlInlineStyleRange right) =>
        left.Start == right.Start
        && left.Length == right.Length
        && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
        && string.Equals(left.Href, right.Href, StringComparison.Ordinal)
        && string.Equals(left.TargetSlipId, right.TargetSlipId, StringComparison.Ordinal)
        && string.Equals(left.CachedTitle, right.CachedTitle, StringComparison.Ordinal);

    private static IReadOnlyList<ZetlInlineStyleRange> ReconcileSingleReplacement(
        string newText,
        IReadOnlyList<ZetlInlineStyleRange> ranges,
        int start,
        int replacedLength,
        int replacementLength)
    {
        var end = start + replacedLength;
        var delta = replacementLength - replacedLength;
        var result = new List<ZetlInlineStyleRange>();
        foreach (var range in ranges)
        {
            var rangeStart = range.Start;
            var rangeEnd = range.Start + range.Length;
            if (rangeEnd <= start)
            {
                result.Add(range);
            }
            else if (rangeStart >= end)
            {
                result.Add(range with { Start = range.Start + delta });
            }
            else if (replacedLength == 0)
            {
                if (start <= rangeStart)
                {
                    result.Add(range with { Start = range.Start + replacementLength });
                }
                else if (start < rangeEnd)
                {
                    result.Add(range with { Length = range.Length + replacementLength });
                }
                else
                {
                    result.Add(range);
                }
            }
            else
            {
                var leftLength = Math.Max(0, start - rangeStart);
                var rightLength = Math.Max(0, rangeEnd - end);
                var newLength = leftLength + replacementLength + rightLength;
                if (newLength <= 0)
                {
                    continue;
                }

                result.Add(range with
                {
                    Start = rangeStart < start ? rangeStart : start,
                    Length = newLength
                });
            }
        }

        return ZetlInlineStyles.Normalize(newText, result);
    }

    private static int CommonPrefixLength(string left, string right)
    {
        var max = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < max && left[index] == right[index])
        {
            index++;
        }

        return index;
    }
}
