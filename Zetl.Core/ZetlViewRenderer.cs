using ZETL.Contracts;

namespace ZETL;

/// <summary>
/// A group of slips under one heading, produced by <see cref="ZetlViewRenderer.BuildGroups"/>:
/// either one bucket (default views) or one named section merging buckets. Depth
/// drives heading level/indentation; HeaderBucket supplies TSV header cells.
/// </summary>
internal sealed record ZetlViewGroup(
    string Heading,
    int Depth,
    ZetlBucketSnapshot? HeaderBucket,
    IReadOnlyList<ZetlSlipSnapshot> Slips)
{
    // Cascading outline number for this group (e.g. "1", "1.1", "2"), assigned in
    // document order by BuildGroups. Renderers prefix the heading with it only when
    // the view sets NumberHeadings.
    public string OutlineNumber { get; init; } = "";

    // Optional per-section heading styling (custom-section views only). Align is
    // "" / "center" / "right"; HeadingLevel 1/2/3 overrides the depth-based level
    // (0 = automatic). Honored by HTML / PDF / on-screen; Markdown uses only the level.
    public string HeadingAlign { get; init; } = "";

    public bool HeadingBold { get; init; }

    public int HeadingLevel { get; init; }

    // The heading level the renderers use: an explicit per-section level, else the
    // depth-based default (h2 for top level).
    public int EffectiveLevel => HeadingLevel > 0
        ? Math.Clamp(HeadingLevel, 1, 6)
        : Math.Min(6, 2 + Depth);
}

// Assigns cascading outline numbers (1, 1.1, 1.1.1) to groups in document order. A
// shallower level that was skipped (an empty parent bucket) is treated as 1 rather
// than 0, so a deep-first group never reads as "0.1".
internal sealed class ZetlOutlineNumberer
{
    private readonly List<int> counters = [];

    public string Next(int depth)
    {
        while (counters.Count <= depth)
        {
            counters.Add(0);
        }

        if (counters.Count > depth + 1)
        {
            counters.RemoveRange(depth + 1, counters.Count - (depth + 1));
        }

        counters[depth]++;
        for (var i = 0; i < counters.Count; i++)
        {
            if (counters[i] == 0)
            {
                counters[i] = 1;
            }
        }

        return string.Join('.', counters);
    }
}

/// <summary>
/// Renders a project's slips through a <see cref="ZetlViewDocument"/>. A pure
/// projection over the public snapshot DTOs — it never mutates slips — so both
/// Zetl and Kastn can render the same view from the same data, and re-rendering
/// always reflects current slips. The Formatted/Plain/TSV kinds reproduce Zetl's
/// compile output; Markdown/HTML are document artifacts; PDF is rendered by the
/// head from the same <see cref="BuildGroups"/> grouping.
/// </summary>
internal static class ZetlViewRenderer
{
    public static string Render(
        ZetlProjectSnapshot project,
        IReadOnlyList<ZetlSlipSnapshot> slips,
        ZetlViewDocument view,
        IReadOnlyDictionary<string, ZetlPictureContent>? pictures = null)
    {
        var groups = BuildGroups(project, slips, view);
        return view.Kind switch
        {
            ZetlViewKinds.Plain => RenderPlain(groups),
            ZetlViewKinds.Tsv => RenderTsv(project, groups, view.TsvRowLength),
            ZetlViewKinds.Markdown => RenderMarkdown(project, groups, view, pictures),
            ZetlViewKinds.Html => RenderHtml(project, groups, view, pictures),
            // PDF is binary; the head renders it from BuildGroups. Return a note so
            // any text surface (e.g. a preview) explains how to get the PDF.
            ZetlViewKinds.Pdf => "This is a PDF view — use Export to save a .pdf file.",
            _ => RenderFormatted(project, groups)
        };
    }

    /// <summary>
    /// Group the given slips for rendering: one group per bucket (in project bucket
    /// order, only buckets with slips) by default, or one group per declared
    /// <see cref="ZetlViewDocument.Sections"/> section (merging the named buckets'
    /// slips under the section title) when the view defines sections. Empty groups
    /// are omitted. Shared by the text renderer and the head's PDF renderer.
    /// </summary>
    public static IReadOnlyList<ZetlViewGroup> BuildGroups(
        ZetlProjectSnapshot project,
        IReadOnlyList<ZetlSlipSnapshot> slips,
        ZetlViewDocument view)
    {
        var bucketsById = project.Buckets.ToDictionary(bucket => bucket.Id);
        var slipsByBucketId = slips
            .Where(slip => bucketsById.ContainsKey(slip.BucketId) && !slip.ExcludedFromViews)
            .GroupBy(slip => slip.BucketId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ZetlSlipSnapshot>)group.ToList(),
                StringComparer.Ordinal);

        if (view.Sections.Count > 0)
        {
            return BuildSectionGroups(project, view, slipsByBucketId);
        }

        var groups = new List<ZetlViewGroup>();
        var numberer = new ZetlOutlineNumberer();
        foreach (var bucket in project.Buckets)
        {
            if (slipsByBucketId.TryGetValue(bucket.Id, out var bucketSlips) && bucketSlips.Count > 0)
            {
                var depth = BucketDepth(bucket, bucketsById);
                groups.Add(new ZetlViewGroup(bucket.Name.Trim(), depth, bucket, bucketSlips)
                {
                    OutlineNumber = numberer.Next(depth),
                    HeadingAlign = bucket.HeadingAlign,
                    HeadingBold = bucket.HeadingBold,
                    HeadingLevel = bucket.HeadingLevel
                });
            }
        }

        return groups;
    }

    private static IReadOnlyList<ZetlViewGroup> BuildSectionGroups(
        ZetlProjectSnapshot project,
        ZetlViewDocument view,
        Dictionary<string, IReadOnlyList<ZetlSlipSnapshot>> slipsByBucketId)
    {
        var bucketsByName = project.Buckets
            .GroupBy(bucket => bucket.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var groups = new List<ZetlViewGroup>();
        var numberer = new ZetlOutlineNumberer();
        foreach (var section in view.Sections)
        {
            var sectionSlips = new List<ZetlSlipSnapshot>();
            ZetlBucketSnapshot? headerBucket = null;
            foreach (var bucketName in section.Buckets)
            {
                if (!bucketsByName.TryGetValue(bucketName.Trim(), out var matched))
                {
                    continue;
                }

                foreach (var bucket in matched)
                {
                    headerBucket ??= bucket;
                    if (slipsByBucketId.TryGetValue(bucket.Id, out var bucketSlips))
                    {
                        sectionSlips.AddRange(bucketSlips);
                    }
                }
            }

            if (sectionSlips.Count > 0)
            {
                // Sections are flat (depth 0); the section title is the heading, with
                // the section's optional heading styling carried onto the group.
                groups.Add(new ZetlViewGroup(section.Title.Trim(), 0, headerBucket, sectionSlips)
                {
                    OutlineNumber = numberer.Next(0),
                    HeadingAlign = section.HeadingAlign,
                    HeadingBold = section.HeadingBold,
                    HeadingLevel = section.HeadingLevel
                });
            }
        }

        return groups;
    }

    private static string RenderFormatted(ZetlProjectSnapshot project, IReadOnlyList<ZetlViewGroup> groups)
    {
        var parts = new List<string> { project.Name.Trim(), "" };
        foreach (var group in groups)
        {
            parts.Add(IndentedLine(group.Heading, group.Depth));
            parts.AddRange(group.Slips.Select(slip => IndentedText(SlipText(slip), group.Depth + 1)));
            parts.Add("");
        }

        return string.Join(Environment.NewLine, parts).TrimEnd();
    }

    private static string RenderPlain(IReadOnlyList<ZetlViewGroup> groups)
    {
        return string.Join(
            Environment.NewLine,
            groups
                .SelectMany(group => group.Slips)
                .Select(slip => SlipText(slip).Trim())
                .Where(text => text.Length > 0));
    }

    private static string RenderTsv(
        ZetlProjectSnapshot project,
        IReadOnlyList<ZetlViewGroup> groups,
        int viewRowLength)
    {
        var parts = new List<string> { project.Name.Trim() };
        foreach (var group in groups)
        {
            parts.Add(group.Heading);
            var headers = group.HeaderBucket is null ? [] : HeaderCells(group.HeaderBucket);
            if (headers.Count > 0)
            {
                parts.Add(string.Join('\t', headers));
            }

            var rowLength = headers.Count > 0 ? headers.Count : Math.Max(1, viewRowLength);
            var cells = group.Slips
                .Select(slip => NormalizeTsvCell(SlipText(slip)))
                .Where(text => text.Length > 0)
                .ToList();
            for (var i = 0; i < cells.Count; i += rowLength)
            {
                parts.Add(string.Join('\t', cells.Skip(i).Take(rowLength)));
            }

            parts.Add("");
        }

        return string.Join(Environment.NewLine, parts).TrimEnd();
    }

    // Heading text, optionally prefixed with the group's cascading outline number.
    // Public so the head's PDF and on-screen renderers number headings consistently.
    public static string HeadingText(ZetlViewGroup group, ZetlViewDocument view) =>
        view.NumberHeadings && group.OutlineNumber.Length > 0
            ? $"{group.OutlineNumber} {group.Heading}"
            : group.Heading;

    public static string NormalizeHeadingAlign(string? align)
    {
        var value = (align ?? "").Trim().ToLowerInvariant();
        return value is "center" or "right" ? value : "left";
    }

    // Inline CSS for a section heading's align/bold (the heading level handles size).
    // Returns "" when there is nothing to style.
    private static string HeadingStyleAttribute(ZetlViewGroup group)
    {
        var rules = new List<string>();
        var align = NormalizeHeadingAlign(group.HeadingAlign);
        if (align is "center" or "right")
        {
            rules.Add($"text-align:{align}");
        }

        if (group.HeadingBold)
        {
            rules.Add("font-weight:700");
        }

        return rules.Count == 0 ? "" : $" style=\"{string.Join(';', rules)}\"";
    }

    // The effective document title for the rich kinds (Markdown / HTML / PDF), or
    // null when the view hides it. A non-empty view Title overrides the project name.
    public static string? DocumentTitle(ZetlProjectSnapshot project, ZetlViewDocument view)
    {
        if (!view.ShowTitle)
        {
            return null;
        }

        var custom = view.Title?.Trim();
        return string.IsNullOrEmpty(custom) ? project.Name.Trim() : custom;
    }

    private static string RenderMarkdown(
        ZetlProjectSnapshot project,
        IReadOnlyList<ZetlViewGroup> groups,
        ZetlViewDocument view,
        IReadOnlyDictionary<string, ZetlPictureContent>? pictures)
    {
        var listStyle = ZetlViewListStyles.Normalize(view.ListStyle);
        var documentTitle = DocumentTitle(project, view);
        var parts = documentTitle is null
            ? new List<string>()
            : new List<string> { $"# {documentTitle}", "" };
        foreach (var group in groups)
        {
            var level = group.EffectiveLevel;
            parts.Add($"{new string('#', level)} {HeadingText(group, view)}");
            parts.Add("");
            var itemNumber = 1;
            foreach (var slip in group.Slips)
            {
                if (slip.Type == ZetlSlipType.Picture)
                {
                    var caption = PictureCaption(slip);
                    if (pictures?.TryGetValue(slip.Id, out var picture) == true)
                    {
                        parts.Add($"![{EscapeMarkdownAlt(caption)}]({DataUri(picture)})");
                        if (!string.IsNullOrWhiteSpace(slip.Text))
                        {
                            parts.Add($"*{slip.Text.Trim()}*");
                        }
                    }
                    else
                    {
                        parts.Add($"- [Picture: {caption}]");
                    }

                    continue;
                }

                var lines = SlipText(slip)
                    .ReplaceLineEndings("\n")
                    .Split('\n')
                    .Select(line => line.TrimEnd())
                    .ToList();
                if (lines.All(line => line.Length == 0))
                {
                    continue;
                }

                if (listStyle == ZetlViewListStyles.Paragraph)
                {
                    parts.AddRange(lines);
                    parts.Add("");
                    continue;
                }

                // A slip that already carries its own list markup (e.g. a checkbox
                // list from the editor) is emitted verbatim, so GFM task items stay
                // interactive instead of being nested under a redundant bucket marker
                // (which produced "- - [ ] x": doubled and not a valid task item).
                var firstContentLine = lines.First(line => line.Length > 0);
                if (StartsWithMarkdownListMarker(firstContentLine))
                {
                    parts.AddRange(lines.Where(line => line.Length > 0));
                    continue;
                }

                var marker = listStyle switch
                {
                    ZetlViewListStyles.Ordered => $"{itemNumber++}. ",
                    ZetlViewListStyles.Task => "- [ ] ",
                    _ => "- "
                };
                var indent = new string(' ', marker.Length);
                parts.Add($"{marker}{lines[0].TrimStart()}");
                parts.AddRange(lines.Skip(1).Select(line => $"{indent}{line}"));
            }

            parts.Add("");
        }

        return string.Join(Environment.NewLine, parts).TrimEnd();
    }

    private static string RenderHtml(
        ZetlProjectSnapshot project,
        IReadOnlyList<ZetlViewGroup> groups,
        ZetlViewDocument view,
        IReadOnlyDictionary<string, ZetlPictureContent>? pictures)
    {
        var listStyle = ZetlViewListStyles.Normalize(view.ListStyle);
        var documentTitle = DocumentTitle(project, view);
        // The <title> tab label always needs a value, even when the on-page heading
        // is hidden; fall back to the project name there.
        var headTitle = Escape((documentTitle ?? project.Name).Trim());
        var parts = new List<string>
        {
            "<!DOCTYPE html>",
            "<html lang=\"en\">",
            "<head>",
            "<meta charset=\"utf-8\" />",
            $"<title>{headTitle}</title>",
            "<style>",
            "body { font-family: system-ui, -apple-system, sans-serif; max-width: 48rem; "
                + "margin: 2rem auto; padding: 0 1rem; line-height: 1.5; }",
            "figure { margin: 1rem 0 1.5rem; }",
            "img { display: block; max-width: 100%; height: auto; border-radius: .35rem; }",
            "figcaption { margin-top: .4rem; color: #666; font-size: .9rem; }",
            "</style>",
            "</head>",
            "<body>"
        };
        if (documentTitle is not null)
        {
            parts.Add($"<h1>{Escape(documentTitle)}</h1>");
        }

        var isList = listStyle != ZetlViewListStyles.Paragraph;
        var openTag = listStyle switch
        {
            ZetlViewListStyles.Ordered => "<ol>",
            ZetlViewListStyles.Task => "<ul style=\"list-style:none;padding-left:1.1em\">",
            _ => "<ul>"
        };
        var closeTag = listStyle == ZetlViewListStyles.Ordered ? "</ol>" : "</ul>";

        foreach (var group in groups)
        {
            var level = group.EffectiveLevel;
            parts.Add($"<h{level}{HeadingStyleAttribute(group)}>{Escape(HeadingText(group, view))}</h{level}>");

            if (group.Slips.Count == 0)
            {
                continue;
            }

            var listOpen = false;
            foreach (var slip in group.Slips)
            {
                if (slip.Type == ZetlSlipType.Picture)
                {
                    if (listOpen)
                    {
                        parts.Add(closeTag);
                        listOpen = false;
                    }

                    var caption = PictureCaption(slip);
                    if (pictures?.TryGetValue(slip.Id, out var picture) == true)
                    {
                        parts.Add("<figure>");
                        parts.Add($"<img src=\"{DataUri(picture)}\" alt=\"{EscapeAttribute(caption)}\" />");
                        if (!string.IsNullOrWhiteSpace(slip.Text))
                        {
                            parts.Add($"<figcaption>{Escape(slip.Text.Trim())}</figcaption>");
                        }
                        parts.Add("</figure>");
                    }
                    else
                    {
                        parts.Add($"<p>[Picture: {Escape(caption)}]</p>");
                    }

                    continue;
                }

                var text = SlipText(slip);
                if (text.Trim().Length == 0)
                {
                    continue;
                }

                // Slip text is Markdown; render its paragraph/list blocks to inline
                // HTML (the parser escapes literal runs). Literal views stay verbatim.
                var align = SlipAlignment(slip);
                var style = align == "left" ? "" : $" style=\"text-align:{align}\"";
                var inner = ZetlMarkdown.BlocksToHtml(text);
                if (isList)
                {
                    if (!listOpen)
                    {
                        parts.Add(openTag);
                        listOpen = true;
                    }

                    var glyph = listStyle == ZetlViewListStyles.Task ? "☐ " : "";
                    parts.Add($"<li{style}>{glyph}{inner}</li>");
                }
                else
                {
                    parts.Add($"<div{style}>{inner}</div>");
                }
            }

            if (listOpen)
            {
                parts.Add(closeTag);
            }
        }

        parts.Add("</body>");
        parts.Add("</html>");
        return string.Join(Environment.NewLine, parts);
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string EscapeAttribute(string text) =>
        Escape(text).Replace("\"", "&quot;");

    // Whether a line already begins with a Markdown list marker (bullet, task
    // checkbox, or ordered). Such a slip is emitted verbatim by the Markdown view
    // rather than wrapped in another bucket-level marker.
    private static bool StartsWithMarkdownListMarker(string line)
    {
        var text = line.TrimStart();
        if (text.StartsWith("- ", StringComparison.Ordinal)
            || text.StartsWith("* ", StringComparison.Ordinal)
            || text.StartsWith("+ ", StringComparison.Ordinal))
        {
            return true;
        }

        var digits = 0;
        while (digits < text.Length && char.IsDigit(text[digits]))
        {
            digits++;
        }

        return digits > 0
            && digits + 1 < text.Length
            && (text[digits] == '.' || text[digits] == ')')
            && text[digits + 1] == ' ';
    }

    private static string EscapeMarkdownAlt(string text) =>
        text.Replace("[", "\\[").Replace("]", "\\]");

    // Per-slip block alignment, normalized to one of left/center/right. Honored as
    // a block style by the HTML/PDF renderers and the on-screen View; the literal
    // and Markdown renderers ignore it (Markdown has no alignment).
    public static string SlipAlignment(ZetlSlipSnapshot slip)
    {
        var value = slip.Align?.Trim().ToLowerInvariant();
        return value is "center" or "right" ? value : "left";
    }

    private static string SlipText(ZetlSlipSnapshot slip) =>
        slip.Type == ZetlSlipType.Picture
            ? $"[Picture: {PictureCaption(slip)}]"
            : string.IsNullOrWhiteSpace(slip.Text) ? slip.Title : slip.Text;

    private static string PictureCaption(ZetlSlipSnapshot slip) =>
        !string.IsNullOrWhiteSpace(slip.Text)
            ? slip.Text.Trim()
            : string.IsNullOrWhiteSpace(slip.Title) ? "Picture" : slip.Title.Trim();

    private static string DataUri(ZetlPictureContent picture) =>
        $"data:image/png;base64,{Convert.ToBase64String(picture.Bytes)}";

    private static int BucketDepth(
        ZetlBucketSnapshot bucket,
        Dictionary<string, ZetlBucketSnapshot> bucketsById)
    {
        var depth = 0;
        var parentId = bucket.ParentBucketId;
        while (parentId is not null && depth < bucketsById.Count)
        {
            depth++;
            parentId = bucketsById.TryGetValue(parentId, out var parent)
                ? parent.ParentBucketId
                : null;
        }

        return depth;
    }

    private static string IndentedLine(string text, int depth) =>
        $"{new string('\t', Math.Max(0, depth))}{text.Trim()}";

    private static string IndentedText(string text, int depth)
    {
        var prefix = new string('\t', Math.Max(0, depth));
        return string.Join(
            Environment.NewLine,
            text.ReplaceLineEndings("\n")
                .Split('\n')
                .Select(line => $"{prefix}{line.TrimEnd()}"));
    }

    private static string NormalizeTsvCell(string text) =>
        text.ReplaceLineEndings(" ").Replace('\t', ' ').Trim();

    private static IReadOnlyList<string> HeaderCells(ZetlBucketSnapshot bucket) =>
        (bucket.Settings.DefaultStartingText ?? "")
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Select(NormalizeTsvCell)
            .Where(text => text.Length > 0)
            .ToList();
}
