using ZETL.Contracts;

namespace ZETL;

// A slip's resolved list kinds. Block governs how the whole slip renders; Outer
// is the list it renders in ("" when none); Inner is its own list kind layered
// inside a different bucket list style ("" when none).
internal readonly record struct ZetlSlipListKinds(string Block, string Outer, string Inner)
{
    public bool IsCheckable => Outer == ZetlBlockKinds.Task || Inner == ZetlBlockKinds.Task;
}

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

    // How Kastn renders this group: "" (a normal section), or "group"/"table"/"latex"
    // when the source bucket is a structural container.
    public string RenderKind { get; init; } = "";

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
/// application from the same <see cref="BuildGroups"/> grouping.
/// </summary>
internal static class ZetlViewRenderer
{
    public static string Render(
        ZetlProjectSnapshot project,
        IReadOnlyList<ZetlSlipSnapshot> slips,
        ZetlViewDocument view,
        IReadOnlyDictionary<string, ZetlPictureContent>? pictures = null,
        bool preferSlipKindOverBucketKind = false)
    {
        var groups = BuildGroups(project, slips, view);
        return view.Kind switch
        {
            ZetlViewKinds.Plain => RenderPlain(groups),
            ZetlViewKinds.Tsv => RenderTsv(project, groups, view.TsvRowLength),
            ZetlViewKinds.Markdown => RenderMarkdown(project, groups, view, pictures, preferSlipKindOverBucketKind),
            ZetlViewKinds.Html => RenderHtml(project, groups, view, pictures, preferSlipKindOverBucketKind),
            // PDF is binary; Kastn renders it from BuildGroups. Return a note so
            // any text surface (e.g. a preview) explains how to get the PDF.
            ZetlViewKinds.Pdf => "This is a PDF view — use Export to save a .pdf file.",
            _ => RenderFormatted(project, groups)
        };
    }

    // Whether copying or exporting this view kind carries inline slip formatting
    // (emphasis, code, links, and list/task markup) into the produced artifact.
    // The literal compile kinds (Formatted/Plain/TSV) emit slip text verbatim, so
    // the Markdown markup would surface as raw characters instead of formatting;
    // Markdown/HTML/PDF each translate it. Mirrors the Render dispatch above.
    public static bool ExportPreservesFormatting(string? kind) =>
        kind is ZetlViewKinds.Markdown or ZetlViewKinds.Html or ZetlViewKinds.Pdf;

    // Whether copying or exporting this view kind carries per-slip and heading
    // alignment. Only the block-laying document kinds do; Markdown has no alignment
    // syntax and the literal kinds ignore it (see SlipAlignment). The on-screen
    // reader honors alignment regardless, so this is purely about the artifact.
    public static bool ExportPreservesAlignment(string? kind) =>
        kind is ZetlViewKinds.Html or ZetlViewKinds.Pdf;

    // Font family, point size, and text color are block styles. HTML and PDF can
    // carry them; Markdown has no portable equivalents and the compile kinds are
    // deliberately literal.
    public static bool ExportPreservesTypography(string? kind) =>
        kind is ZetlViewKinds.Html or ZetlViewKinds.Pdf;

    /// <summary>
    /// Group the given slips for rendering: one group per bucket (in project bucket
    /// order, including empty ancestors that contain visible descendant slips) by default, or one group per declared
    /// <see cref="ZetlViewDocument.Sections"/> section (merging the named buckets'
    /// slips under the section title) when the view defines sections. Empty groups
    /// with no visible descendants are omitted. Shared by the text renderer and
    /// Kastn's PDF renderer.
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
        var childrenByParentId = project.Buckets
            .GroupBy(bucket => bucket.ParentBucketId ?? "", StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ZetlBucketSnapshot>)group.ToList(),
                StringComparer.Ordinal);
        var emittedBucketIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var bucket in project.Buckets.Where(bucket => bucket.ParentBucketId is null))
        {
            AddRenderableBucket(bucket);
        }

        foreach (var bucket in project.Buckets)
        {
            AddRenderableBucket(bucket);
        }

        return groups;

        bool HasVisibleContent(ZetlBucketSnapshot bucket)
        {
            if (slipsByBucketId.TryGetValue(bucket.Id, out var bucketSlips) && bucketSlips.Count > 0)
            {
                return true;
            }

            return childrenByParentId.TryGetValue(bucket.Id, out var children)
                && children.Any(HasVisibleContent);
        }

        void AddRenderableBucket(ZetlBucketSnapshot bucket)
        {
            if (!emittedBucketIds.Add(bucket.Id) || !HasVisibleContent(bucket))
            {
                return;
            }

            slipsByBucketId.TryGetValue(bucket.Id, out var bucketSlips);
            var depth = ZetlTreeText.BucketDepth(bucket, bucketsById);
            groups.Add(new ZetlViewGroup(bucket.Name.Trim(), depth, bucket, bucketSlips ?? [])
            {
                OutlineNumber = numberer.Next(depth),
                HeadingAlign = bucket.HeadingAlign,
                HeadingBold = bucket.HeadingBold,
                HeadingLevel = bucket.HeadingLevel,
                RenderKind = ZetlBucketRenderKinds.Normalize(bucket.RenderKind)
            });

            if (!childrenByParentId.TryGetValue(bucket.Id, out var children))
            {
                return;
            }

            foreach (var child in children)
            {
                AddRenderableBucket(child);
            }
        }
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
            parts.Add(ZetlTreeText.IndentedLine(group.Heading, group.Depth));
            parts.AddRange(group.Slips.Select(slip =>
                ZetlTreeText.IndentedText(SlipText(slip), group.Depth + 1)));
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
    // Public so the PDF and on-screen renderers number headings consistently.
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
        IReadOnlyDictionary<string, ZetlPictureContent>? pictures,
        bool preferSlipKindOverBucketKind)
    {
        var documentTitle = DocumentTitle(project, view);
        var parts = documentTitle is null
            ? new List<string>()
            : new List<string> { $"# {documentTitle}", "" };
        // One id set for the whole render: the wiki-link resolver runs per link,
        // so a per-call scan of project.Slips would go quadratic on big projects.
        var slipIds = project.Slips.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var level = group.EffectiveLevel;
            parts.Add($"{new string('#', level)} {HeadingText(group, view)}");
            parts.Add("");
            // Ordered notes are numbered over their run; any non-ordered note, picture,
            // or block-structured note restarts the count.
            var orderedRun = 0;
            foreach (var slip in group.Slips)
            {
                if (slip.Type == ZetlSlipType.Picture)
                {
                    orderedRun = 0;
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

                var slipText = ZetlMarkdown.ResolveWikiLinksInNote(
                    ZetlMarkdown.ApplyInlineStyleMarkers(
                        SlipText(slip),
                        EffectiveInlineStyles(slip, SlipText(slip))),
                    slipIds.Contains);
                var listKinds = ResolveListKinds(project, slip, preferSlipKindOverBucketKind);
                var kind = listKinds.Block;

                // A divider note has no content: emit a thematic break and move on.
                if (kind == ZetlBlockKinds.Divider)
                {
                    orderedRun = 0;
                    parts.Add("---");
                    parts.Add("");
                    continue;
                }

                var lines = slipText
                    .ReplaceLineEndings("\n")
                    .Split('\n')
                    .Select(line => line.TrimEnd())
                    .ToList();
                if (lines.All(line => line.Length == 0))
                {
                    continue;
                }

                // Whole-note block kinds emit as their own blocks: a heading softens to
                // bold (kept out of the .md outline); quote and code use GFM syntax.
                if (kind == ZetlBlockKinds.Heading)
                {
                    orderedRun = 0;
                    parts.AddRange(lines.Where(line => line.Length > 0).Select(line => $"**{line}**"));
                    parts.Add("");
                    continue;
                }

                if (kind == ZetlBlockKinds.Quote)
                {
                    orderedRun = 0;
                    parts.AddRange(lines.Select(line => line.Length == 0 ? ">" : $"> {line}"));
                    parts.Add("");
                    continue;
                }

                if (kind == ZetlBlockKinds.Code)
                {
                    orderedRun = 0;
                    parts.Add("```");
                    parts.AddRange(lines);
                    parts.Add("```");
                    parts.Add("");
                    continue;
                }

                // A note carrying in-body block markup (typed heading/quote/fence/divider)
                // emits as its own blocks rather than under a list marker that would mangle
                // it; softened sub-headings become bold so they stay out of the .md outline.
                if (ZetlMarkdown.ContainsBlockStructure(slipText))
                {
                    orderedRun = 0;
                    parts.AddRange(ZetlMarkdown.SoftenHeadingsForMarkdown(lines));
                    parts.Add("");
                    continue;
                }

                // If the author already typed Markdown list syntax, preserve it.
                // Styling a bucket/slip as a list must not double-prefix "- [ ]".
                if (FirstContentLineStartsWithMarkdownListMarker(lines))
                {
                    orderedRun = 0;
                    parts.AddRange(lines.Where(line => line.Length > 0));
                    continue;
                }

                if (listKinds.Outer.Length > 0)
                {
                    var (markerKind, innerKind) = MarkdownListKinds(listKinds.Outer, listKinds.Inner);
                    var marker = MarkdownOuterListMarker(markerKind, slip.Checked, ref orderedRun)
                        + MarkdownInnerListMarker(innerKind, slip.Checked);
                    EmitMarkedSlipMarkdown(parts, marker, lines);
                    continue;
                }

                orderedRun = 0;
                parts.AddRange(lines);
                parts.Add("");
            }

            parts.Add("");
        }

        // Collapse runs of blank lines so a paragraph's trailing blank and the group
        // separator never stack into a gap (a single blank line is the GFM separator).
        var collapsed = new List<string>(parts.Count);
        foreach (var part in parts)
        {
            if (part.Length == 0 && collapsed.Count > 0 && collapsed[^1].Length == 0)
            {
                continue;
            }

            collapsed.Add(part);
        }

        return string.Join(Environment.NewLine, collapsed).TrimEnd();
    }

    // Emit one list-item note: the marker on the first line, continuation lines hung to
    // the marker's width. No trailing blank, so a run of list notes stays one list.
    private static void EmitMarkedSlipMarkdown(List<string> parts, string marker, IReadOnlyList<string> lines)
    {
        var indent = new string(' ', marker.Length);
        parts.Add($"{marker}{lines[0].TrimStart()}");
        parts.AddRange(lines.Skip(1).Select(line => $"{indent}{line}"));
    }

    public static bool IsListRenderKind(string? kind) =>
        kind is ZetlBlockKinds.Bullet or ZetlBlockKinds.Ordered or ZetlBlockKinds.Task;

    // How a slip's own kind composes with its bucket's list style. Every renderer
    // (on-screen View, Board, Markdown, HTML, PDF) resolves through this so they
    // agree. The bucket is always the slip's own bucket, never the view section it
    // is rendered under.
    public static ZetlSlipListKinds ResolveListKinds(
        ZetlProjectSnapshot project,
        ZetlSlipSnapshot slip,
        bool preferSlipKindOverBucketKind = false)
    {
        var slipKind = slip.Type == ZetlSlipType.Picture ? "" : SlipBlockKind(slip);
        var bucketKind = BucketListKind(project, slip, slipKind, preferSlipKindOverBucketKind);
        if (bucketKind.Length == 0)
        {
            return new ZetlSlipListKinds(
                slipKind,
                IsListRenderKind(slipKind) ? slipKind : "",
                "");
        }

        // A list-kind or plain slip joins the bucket's list; a different list kind
        // layers inside it (a task in a numbered bucket). Whole-slip blocks
        // (heading/quote/code/divider) keep their own kind under the bucket marker.
        var slipIsList = IsListRenderKind(slipKind);
        return new ZetlSlipListKinds(
            slipIsList || slipKind.Length == 0 ? bucketKind : slipKind,
            bucketKind,
            slipIsList && !string.Equals(slipKind, bucketKind, StringComparison.Ordinal) ? slipKind : "");
    }

    private static string BucketListKind(
        ZetlProjectSnapshot project,
        ZetlSlipSnapshot slip,
        string slipKind,
        bool preferSlipKindOverBucketKind)
    {
        if (slip.IgnoreBucketRenderKind
            || (preferSlipKindOverBucketKind && slipKind.Length > 0))
        {
            return "";
        }

        var bucket = project.Buckets.FirstOrDefault(b => b.Id == slip.BucketId);
        return bucket?.RenderKind is ZetlBucketRenderKinds.Bullet
            or ZetlBucketRenderKinds.Ordered
            or ZetlBucketRenderKinds.Task
                ? bucket.RenderKind
                : "";
    }

    public static string MarkdownOuterListMarker(string kind, bool isChecked, ref int orderedRun)
    {
        if (kind == ZetlBlockKinds.Ordered)
        {
            return $"{++orderedRun}. ";
        }

        orderedRun = 0;
        return kind switch
        {
            ZetlBlockKinds.Task => isChecked ? "- [x] " : "- [ ] ",
            ZetlBlockKinds.Bullet => "- ",
            _ => ""
        };
    }

    public static string MarkdownInnerListMarker(string kind, bool isChecked) => kind switch
    {
        ZetlBlockKinds.Task => isChecked ? "[x] " : "[ ] ",
        ZetlBlockKinds.Bullet => "• ",
        ZetlBlockKinds.Ordered => "1. ",
        _ => ""
    };

    private static (string OuterKind, string InnerKind) MarkdownListKinds(string outerKind, string innerKind) =>
        outerKind == ZetlBlockKinds.Task || innerKind == ZetlBlockKinds.Task
            ? (ZetlBlockKinds.Task, "")
            : (outerKind, innerKind);

    public static string HtmlListItemMarker(string outerKind, string innerKind, bool isChecked)
    {
        var outerMarker = outerKind == ZetlBlockKinds.Task ? ZetlMarkdown.TaskCheckboxHtml(isChecked) : "";
        var innerMarker = innerKind switch
        {
            ZetlBlockKinds.Task => ZetlMarkdown.TaskCheckboxHtml(isChecked),
            ZetlBlockKinds.Bullet => "• ",
            ZetlBlockKinds.Ordered => "1. ",
            _ => ""
        };
        return outerMarker + innerMarker;
    }

    private static string RenderHtml(
        ZetlProjectSnapshot project,
        IReadOnlyList<ZetlViewGroup> groups,
        ZetlViewDocument view,
        IReadOnlyDictionary<string, ZetlPictureContent>? pictures,
        bool preferSlipKindOverBucketKind)
    {
        var documentTitle = DocumentTitle(project, view);
        // The <title> tab label always needs a value, even when the on-page heading
        // is hidden; fall back to the project name there.
        var headTitle = ZetlHtml.Escape((documentTitle ?? project.Name).Trim());
        var parts = new List<string>
        {
            "<!DOCTYPE html>",
            "<html lang=\"en\">",
            "<head>",
            "<meta charset=\"utf-8\" />",
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; form-action 'none'; base-uri 'none'\" />",
            $"<title>{headTitle}</title>",
            "<style>",
            "body { font-family: system-ui, -apple-system, sans-serif; max-width: 48rem; "
                + "margin: 2rem auto; padding: 0 1rem; line-height: 1.5; }",
            "figure { margin: 1rem 0 1.5rem; }",
            "img { display: block; max-width: 100%; height: auto; border-radius: .35rem; }",
            "figcaption { margin-top: .4rem; color: #666; font-size: .9rem; }",
            "section.kastn-group { border: 1px solid #ccc; border-radius: 6px; "
                + "padding: .1rem 1rem 1rem; margin: 1rem 0; }",
            ".kastn-blocked-link { color: #666; text-decoration: underline dotted; cursor: not-allowed; }",
            "</style>",
            "</head>",
            "<body>"
        };
        if (documentTitle is not null)
        {
            parts.Add($"<h1>{ZetlHtml.Escape(documentTitle)}</h1>");
        }

        // One id set for the whole render: the wiki-link resolver runs per link,
        // so a per-call scan of project.Slips would go quadratic on big projects.
        var slipIds = project.Slips.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            // A container bucket ("group") wraps its heading and slips in a bordered box.
            var isGroup = group.RenderKind == ZetlBucketRenderKinds.Group;
            if (isGroup)
            {
                parts.Add("<section class=\"kastn-group\">");
            }

            var level = group.EffectiveLevel;
            parts.Add($"<h{level}{HeadingStyleAttribute(group)}>{ZetlHtml.Escape(HeadingText(group, view))}</h{level}>");

            if (group.Slips.Count == 0)
            {
                if (isGroup)
                {
                    parts.Add("</section>");
                }

                continue;
            }

            // The open list's kind ("bullet"/"ordered"/"task"), or null when no list is
            // open. Each note carries its own kind, so a run of same-kind notes shares
            // one <ul>/<ol> and a kind change (or a paragraph/picture) closes it.
            string? openKind = null;
            void CloseList()
            {
                if (openKind is not null)
                {
                    parts.Add(openKind == ZetlBlockKinds.Ordered ? "</ol>" : "</ul>");
                    openKind = null;
                }
            }

            foreach (var slip in group.Slips)
            {
                if (slip.Type == ZetlSlipType.Picture)
                {
                    CloseList();
                    var caption = PictureCaption(slip);
                    var typographyStyle = SlipStyleAttribute(slip, includeAlignment: false);
                    if (pictures?.TryGetValue(slip.Id, out var picture) == true)
                    {
                        parts.Add($"<figure id=\"{slip.Id}\"{typographyStyle}>");
                        parts.Add($"<img src=\"{DataUri(picture)}\" alt=\"{ZetlHtml.EscapeAttribute(caption)}\" />");
                        if (!string.IsNullOrWhiteSpace(slip.Text))
                        {
                            parts.Add($"<figcaption>{ZetlHtml.Escape(slip.Text.Trim())}</figcaption>");
                        }
                        parts.Add("</figure>");
                    }
                    else
                    {
                        parts.Add($"<p id=\"{slip.Id}\"{typographyStyle}>[Picture: {ZetlHtml.Escape(caption)}]</p>");
                    }

                    continue;
                }

                var text = SlipText(slip);
                var listKinds = ResolveListKinds(project, slip, preferSlipKindOverBucketKind);
                var kind = listKinds.Block;

                // A whole-note block (heading/quote/code/divider) renders its synthesized
                // block on its own; a divider carries no text, so it is handled before the
                // empty-text skip.
                if (kind is ZetlBlockKinds.Heading or ZetlBlockKinds.Quote
                    or ZetlBlockKinds.Code or ZetlBlockKinds.Divider)
                {
                    CloseList();
                    var typographyStyle = SlipStyleAttribute(slip, includeAlignment: false);
                    parts.Add($"<div id=\"{slip.Id}\"{typographyStyle}>" + ZetlMarkdown.BlocksToHtml(ZetlMarkdown.BlocksForNote(kind, text, EffectiveInlineStyles(slip, text)), slipIds.Contains) + "</div>");
                    continue;
                }

                if (text.Trim().Length == 0)
                {
                    continue;
                }

                // Slip text is Markdown; render its paragraph/list blocks to inline
                // HTML (the parser escapes literal runs). Literal views stay verbatim.
                var style = SlipStyleAttribute(slip, includeAlignment: true);
                var styledText = ZetlMarkdown.ApplyInlineStyleMarkers(text, EffectiveInlineStyles(slip, text));
                var inner = ZetlMarkdown.BlocksToHtml(styledText, slipIds.Contains);
                if (FirstContentLineStartsWithMarkdownListMarker(text))
                {
                    CloseList();
                    parts.Add($"<div id=\"{slip.Id}\"{style}>{inner}</div>");
                    continue;
                }

                var listKind = listKinds.Outer;
                if (listKind.Length == 0)
                {
                    CloseList();
                    parts.Add($"<div id=\"{slip.Id}\"{style}>{inner}</div>");
                    continue;
                }

                if (openKind != listKind)
                {
                    CloseList();
                    parts.Add(listKind switch
                    {
                        ZetlBlockKinds.Ordered => "<ol>",
                        ZetlBlockKinds.Task => "<ul style=\"list-style:none;padding-left:1.1em\">",
                        _ => "<ul>"
                    });
                    openKind = listKind;
                }

                var marker = HtmlListItemMarker(listKind, listKinds.Inner, slip.Checked);
                parts.Add($"<li id=\"{slip.Id}\"{style}>{marker}{inner}</li>");
            }

            CloseList();
            if (isGroup)
            {
                parts.Add("</section>");
            }
        }

        parts.Add("</body>");
        parts.Add("</html>");
        return string.Join(Environment.NewLine, parts);
    }

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

    private static bool FirstContentLineStartsWithMarkdownListMarker(IEnumerable<string> lines) =>
        lines.FirstOrDefault(line => line.Trim().Length > 0) is { } first
        && StartsWithMarkdownListMarker(first);

    private static bool FirstContentLineStartsWithMarkdownListMarker(string text) =>
        FirstContentLineStartsWithMarkdownListMarker(
            (text ?? "").ReplaceLineEndings("\n").Split('\n'));

    private static string EscapeMarkdownAlt(string text) =>
        text.Replace("[", "\\[").Replace("]", "\\]");

    // The slip's inline styles for rendering: its stored ranges plus per-line
    // ranges for the whole-slip Bold/Italic/Strike flags, so slip-level styling
    // flows through the same marker pipeline as typed and range styling. Emitted
    // per content line (emphasis markers cannot span line breaks) and after any
    // typed block marker so a "- item" line stays a list item; code-kind slips
    // and fenced lines take no styling — their body is literal.
    public static IReadOnlyList<ZetlInlineStyleRange> EffectiveInlineStyles(
        ZetlSlipSnapshot slip,
        string text)
    {
        text ??= "";
        if ((!slip.Bold && !slip.Italic && !slip.Strike)
            || text.Length == 0
            || SlipBlockKind(slip) == ZetlBlockKinds.Code)
        {
            return slip.InlineStyles;
        }

        var styles = new List<ZetlInlineStyleRange>(slip.InlineStyles);
        var inFence = false;
        var lineStart = 0;
        var i = 0;
        while (i <= text.Length)
        {
            if (i < text.Length && text[i] is not ('\n' or '\r'))
            {
                i++;
                continue;
            }

            var line = text[lineStart..i];
            if (ZetlMarkdown.IsCodeFenceLine(line))
            {
                inFence = !inFence;
            }
            else if (!inFence && ZetlMarkdown.StyleTargetWithinLine(line) is { } target)
            {
                AddLineStyles(styles, slip, lineStart + target.Start, target.Length);
            }

            if (i < text.Length && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            i++;
            lineStart = i;
        }

        return styles;
    }

    private static void AddLineStyles(
        List<ZetlInlineStyleRange> styles,
        ZetlSlipSnapshot slip,
        int start,
        int length)
    {
        if (slip.Bold)
        {
            styles.Add(new ZetlInlineStyleRange { Start = start, Length = length, Kind = ZetlInlineStyleKinds.Bold });
        }

        if (slip.Italic)
        {
            styles.Add(new ZetlInlineStyleRange { Start = start, Length = length, Kind = ZetlInlineStyleKinds.Italic });
        }

        if (slip.Strike)
        {
            styles.Add(new ZetlInlineStyleRange { Start = start, Length = length, Kind = ZetlInlineStyleKinds.Strike });
        }
    }

    // Per-slip block alignment, normalized to one of left/center/right. Honored as
    // a block style by the HTML/PDF renderers and the on-screen View; the literal
    // and Markdown renderers ignore it (Markdown has no alignment).
    public static string SlipAlignment(ZetlSlipSnapshot slip)
    {
        var value = slip.Align?.Trim().ToLowerInvariant();
        return value is "center" or "right" ? value : "left";
    }

    // Render-safe typography values for one slip. Storage normalization remains in
    // ZetlSlipTypography; these helpers give the UI/PDF/HTML paths one read contract.
    // Font-family is additionally restricted to a single CSS-safe family name so a
    // hand-edited snapshot cannot append another declaration to an HTML style.
    public static string SlipFontFamily(ZetlSlipSnapshot slip)
    {
        var value = ZetlSlipTypography.NormalizeFontFamily(slip.FontFamily);
        return value.Length > 0
            && value.All(character =>
                char.IsLetterOrDigit(character)
                || character is ' ' or '-' or '_' or '.')
            ? value
            : "";
    }

    public static int SlipFontSize(ZetlSlipSnapshot slip) =>
        ZetlSlipTypography.NormalizeFontSize(slip.FontSize);

    public static string SlipTextColor(ZetlSlipSnapshot slip) =>
        ZetlSlipTypography.NormalizeTextColor(slip.TextColor);

    // Inline style for a slip's outer HTML block. Alignment is omitted for the
    // picture and synthesized-block paths because those paths did not previously
    // apply it; typography is still carried at the whole-slip boundary.
    private static string SlipStyleAttribute(ZetlSlipSnapshot slip, bool includeAlignment)
    {
        var rules = new List<string>();
        if (includeAlignment)
        {
            var align = SlipAlignment(slip);
            if (align != "left")
            {
                rules.Add($"text-align:{align}");
            }
        }

        var fontFamily = SlipFontFamily(slip);
        if (fontFamily.Length > 0)
        {
            rules.Add($"font-family:{fontFamily}");
        }

        var fontSize = SlipFontSize(slip);
        if (fontSize > 0)
        {
            rules.Add($"font-size:{fontSize}pt");
        }

        var textColor = SlipTextColor(slip);
        if (textColor.Length > 0)
        {
            rules.Add($"color:{textColor}");
        }

        return rules.Count == 0
            ? ""
            : $" style=\"{ZetlHtml.EscapeAttribute(string.Join(';', rules))}\"";
    }

    // The note's own block kind in a rendered view, normalized — see ZetlBlockKinds.
    // Authoritative per note: a view never markers slips uniformly.
    public static string SlipBlockKind(ZetlSlipSnapshot slip) => ZetlBlockKinds.Normalize(slip.BlockKind);

    public static string BucketRenderKind(ZetlBucketSnapshot bucket) =>
        ZetlBucketRenderKinds.Normalize(bucket.RenderKind);

    // A slip's displayed text: its body, or its title when the body is blank.
    public static string TextOrTitle(ZetlSlipSnapshot slip) =>
        string.IsNullOrWhiteSpace(slip.Text) ? slip.Title : slip.Text;

    private static string SlipText(ZetlSlipSnapshot slip) =>
        slip.Type == ZetlSlipType.Picture
            ? $"[Picture: {PictureCaption(slip)}]"
            : TextOrTitle(slip);

    private static string PictureCaption(ZetlSlipSnapshot slip) =>
        !string.IsNullOrWhiteSpace(slip.Text)
            ? slip.Text.Trim()
            : string.IsNullOrWhiteSpace(slip.Title) ? "Picture" : slip.Title.Trim();

    private static string DataUri(ZetlPictureContent picture) =>
        $"data:image/png;base64,{Convert.ToBase64String(picture.Bytes)}";

    private static string NormalizeTsvCell(string text) =>
        text.ReplaceLineEndings(" ").Replace('\t', ' ').Trim();

    private static IReadOnlyList<string> HeaderCells(ZetlBucketSnapshot bucket) =>
        (bucket.Settings.DefaultStartingText ?? "")
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Select(NormalizeTsvCell)
            .Where(text => text.Length > 0)
            .ToList();
}
