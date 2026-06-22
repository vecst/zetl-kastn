using System.Text;
using ZETL.Contracts;

namespace ZETL;

/// <summary>
/// One addressable wiki-link token inside a slip's Markdown text. The target id is
/// authoritative; CachedTitle exists only to keep raw text readable and to provide
/// a useful label while the target cannot be resolved.
/// </summary>
internal sealed record ZetlSlipLink(
    int Start,
    int Length,
    string TargetId,
    string CachedTitle);

internal sealed record ZetlResolvedSlipLink(
    ZetlSlipLink Link,
    ZetlSlipSnapshot? Target,
    string DisplayTitle,
    bool IsResolved);

internal sealed record ZetlSlipBacklink(
    string SourceSlipId,
    string SourceTitle,
    string TargetSlipId);

/// <summary>
/// Parser and project-level index for Kastn's <c>[[id|Title]]</c> links. This is a
/// pure projection over immutable snapshots: it never mutates a project or stores
/// backlinks. Malformed tokens remain ordinary text.
/// </summary>
internal static class ZetlSlipLinks
{
    public static IReadOnlyList<ZetlSlipLink> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var links = new List<ZetlSlipLink>();
        var cursor = 0;
        while (cursor < text.Length - 1)
        {
            var start = text.IndexOf("[[", cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var end = text.IndexOf("]]", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            var nestedStart = text.IndexOf("[[", start + 2, StringComparison.Ordinal);
            if (nestedStart >= 0 && nestedStart < end)
            {
                cursor = nestedStart;
                continue;
            }

            var separator = text.IndexOf('|', start + 2, end - start - 2);
            if (separator > start + 2)
            {
                var targetId = text[(start + 2)..separator].Trim();
                var cachedTitle = text[(separator + 1)..end].Trim();
                if (targetId.Length > 0)
                {
                    links.Add(new ZetlSlipLink(
                        start,
                        end + 2 - start,
                        targetId,
                        cachedTitle.Length == 0 ? "Untitled" : cachedTitle));
                }
            }

            cursor = end + 2;
        }

        return links;
    }

    public static IReadOnlyList<ZetlResolvedSlipLink> Resolve(
        string? text,
        ZetlProjectSnapshot project)
    {
        var targets = project.Slips.ToDictionary(slip => slip.Id, StringComparer.Ordinal);
        return Parse(text).Select(link =>
        {
            targets.TryGetValue(link.TargetId, out var target);
            return new ZetlResolvedSlipLink(
                link,
                target,
                target is null ? link.CachedTitle : TitleFor(target),
                target is not null);
        }).ToList();
    }

    public static ZetlSlipLink? FindAt(string? text, int position)
    {
        var length = text?.Length ?? 0;
        var caret = Math.Clamp(position, 0, length);
        return Parse(text).FirstOrDefault(link =>
            caret >= link.Start && caret <= link.Start + link.Length);
    }

    /// <summary>
    /// Rewrites only resolved links whose cached title is stale. Unresolved tokens
    /// are preserved byte-for-byte so deleting a target never destroys the label.
    /// </summary>
    public static string RefreshCachedTitles(string? text, ZetlProjectSnapshot project)
    {
        var source = text ?? "";
        var links = Resolve(source, project);
        if (links.Count == 0)
        {
            return source;
        }

        var result = new StringBuilder(source.Length);
        var cursor = 0;
        foreach (var resolved in links)
        {
            result.Append(source, cursor, resolved.Link.Start - cursor);
            if (resolved.IsResolved)
            {
                result.Append(Format(resolved.Link.TargetId, resolved.DisplayTitle));
            }
            else
            {
                result.Append(source, resolved.Link.Start, resolved.Link.Length);
            }

            cursor = resolved.Link.Start + resolved.Link.Length;
        }

        result.Append(source, cursor, source.Length - cursor);
        return result.ToString();
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<ZetlSlipBacklink>> BuildBacklinkIndex(
        ZetlProjectSnapshot project)
    {
        var knownIds = project.Slips.Select(slip => slip.Id).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, List<ZetlSlipBacklink>>(StringComparer.Ordinal);
        foreach (var source in project.Slips)
        {
            var seenTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var link in Parse(source.Text))
            {
                if (!knownIds.Contains(link.TargetId) || !seenTargets.Add(link.TargetId))
                {
                    continue;
                }

                if (!result.TryGetValue(link.TargetId, out var backlinks))
                {
                    backlinks = [];
                    result[link.TargetId] = backlinks;
                }

                backlinks.Add(new ZetlSlipBacklink(source.Id, TitleFor(source), link.TargetId));
            }
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ZetlSlipBacklink>)pair.Value,
            StringComparer.Ordinal);
    }

    public static string Format(string targetId, string title)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new ArgumentException("A link target id is required.", nameof(targetId));
        }

        return $"[[{targetId.Trim()}|{EscapeCachedTitle(NormalizeTitle(title))}]]";
    }

    public static string TitleFor(ZetlSlipSnapshot slip)
    {
        var source = string.IsNullOrWhiteSpace(slip.Title) ? slip.Text : slip.Title;
        return NormalizeTitle(source);
    }

    private static string NormalizeTitle(string? title)
    {
        var words = (title ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalized = string.Join(' ', words);
        if (normalized.Length == 0)
        {
            return "Untitled";
        }

        return normalized.Length <= 80 ? normalized : $"{normalized[..77]}...";
    }

    private static string EscapeCachedTitle(string title) =>
        title.Replace("]]", "] ]", StringComparison.Ordinal);
}
