using System.Text;
using ZETL.Contracts;

namespace ZETL;

// The hand-rolled Markdown layer for slip text (UI roadmap item 7). Intentionally a
// small subset — inline emphasis, code, and links — parsed into one AST that every
// renderer walks: HTML here, and MigraDoc/Avalonia inlines in the applications. Unmatched
// delimiters fall back to literal text, so plain text is always valid input and the
// literal views (Formatted/Plain/TSV) can keep ignoring it and render verbatim.

internal abstract record ZetlInline;

internal sealed record ZetlTextRun(string Text) : ZetlInline;

internal sealed record ZetlCodeRun(string Text) : ZetlInline;

// Kind is "bold", "italic", or "strike".
internal sealed record ZetlEmphasis(string Kind, IReadOnlyList<ZetlInline> Children) : ZetlInline;

internal sealed record ZetlLink(string Url, IReadOnlyList<ZetlInline> Children) : ZetlInline;

internal sealed record ZetlWikiLink(string TargetId, string CachedTitle) : ZetlInline;

// Block-level structure within one slip: consecutive plain lines form a paragraph,
// consecutive same-kind list lines form a list. Lists are flat (no nesting inside a
// slip — bucket nesting is the renderer's job).
internal abstract record ZetlBlock;

internal sealed record ZetlParagraphBlock(IReadOnlyList<IReadOnlyList<ZetlInline>> Lines) : ZetlBlock;

internal sealed record ZetlListItem(IReadOnlyList<ZetlInline> Inlines, bool Checked);

// Kind is "bullet", "ordered", or "task". Start is the ordered list's first number
// (1 for bullet/task), preserved so renderers can continue a run across slips.
internal sealed record ZetlListBlock(string Kind, IReadOnlyList<ZetlListItem> Items, int Start = 1) : ZetlBlock;

// A "# heading" within a slip. Rendered as a *softened* sub-heading (bold, slightly
// larger) rather than a real document heading, so it never competes with the bucket /
// section outline or the slip title. Level (1-6) is kept only to scale that emphasis.
internal sealed record ZetlHeadingBlock(int Level, IReadOnlyList<ZetlInline> Inlines) : ZetlBlock;

// Consecutive "> " lines form one blockquote; each line keeps its own inlines.
internal sealed record ZetlQuoteBlock(IReadOnlyList<IReadOnlyList<ZetlInline>> Lines) : ZetlBlock;

// A fenced ``` code block. Content is literal — no inline parsing — and Language is the
// optional info string after the opening fence ("" when none).
internal sealed record ZetlCodeBlock(string Text, string Language) : ZetlBlock;

// A "---" thematic break.
internal sealed record ZetlDividerBlock : ZetlBlock;

internal static class ZetlMarkdown
{
    public static IReadOnlyList<ZetlInline> ParseInlines(string text) =>
        ParseInlines(text ?? "", 0, (text ?? "").Length);

    public static string ApplyInlineStyleMarkers(
        string text,
        IReadOnlyList<ZetlInlineStyleRange>? inlineStyles)
    {
        text ??= "";
        var ranges = ZetlInlineStyles.Normalize(text, inlineStyles);
        if (ranges.Count == 0)
        {
            return text;
        }

        var opens = new Dictionary<int, List<InlineMarker>>();
        var closes = new Dictionary<int, List<InlineMarker>>();
        foreach (var range in ranges)
        {
            if (!TryGetMarkers(range, out var open, out var close, out var priority))
            {
                continue;
            }

            var marker = new InlineMarker(open, close, priority);
            AddMarker(opens, range.Start, marker);
            AddMarker(closes, range.Start + range.Length, marker);
        }

        if (opens.Count == 0 && closes.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + ranges.Count * 4);
        for (var i = 0; i < text.Length; i++)
        {
            if (opens.TryGetValue(i, out var openMarkers))
            {
                foreach (var marker in openMarkers.OrderBy(item => item.Priority))
                {
                    builder.Append(marker.Open);
                }
            }

            builder.Append(text[i]);

            if (closes.TryGetValue(i + 1, out var closeMarkers))
            {
                foreach (var marker in closeMarkers.OrderByDescending(item => item.Priority))
                {
                    builder.Append(marker.Close);
                }
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<ZetlInline> ParseInlines(string s, int start, int end)
    {
        var result = new List<ZetlInline>();
        var textStart = start;
        var i = start;

        void FlushText(int upto)
        {
            if (upto > textStart)
            {
                result.Add(new ZetlTextRun(s[textStart..upto]));
            }
        }

        while (i < end)
        {
            var c = s[i];

            // Inline code: literal, no nested parsing.
            if (c == '`')
            {
                var close = s.IndexOf('`', i + 1);
                if (close >= 0 && close < end && close > i + 1)
                {
                    FlushText(i);
                    result.Add(new ZetlCodeRun(s[(i + 1)..close]));
                    i = close + 1;
                    textStart = i;
                    continue;
                }
            }
            // Wiki-link: [[targetId|cachedTitle]].
            else if (c == '[' && i + 1 < end && s[i + 1] == '[')
            {
                var close = s.IndexOf("]]", i + 2, StringComparison.Ordinal);
                if (close >= 0 && close < end)
                {
                    var sep = s.IndexOf('|', i + 2, close - i - 2);
                    if (sep > i + 2)
                    {
                        var targetId = s[(i + 2)..sep].Trim();
                        var cachedTitle = s[(sep + 1)..close].Trim();
                        if (targetId.Length > 0)
                        {
                            FlushText(i);
                            result.Add(new ZetlWikiLink(targetId, cachedTitle.Length == 0 ? "Untitled" : cachedTitle));
                            i = close + 2;
                            textStart = i;
                            continue;
                        }
                    }
                }
            }
            // Link: [text](url). Bracket text is parsed; the URL is literal.
            else if (c == '[')
            {
                var closeBracket = s.IndexOf(']', i + 1);
                if (closeBracket > i + 1
                    && closeBracket + 1 < end
                    && s[closeBracket + 1] == '(')
                {
                    var closeParen = s.IndexOf(')', closeBracket + 2);
                    if (closeParen >= 0 && closeParen < end)
                    {
                        FlushText(i);
                        var children = ParseInlines(s, i + 1, closeBracket);
                        var url = s[(closeBracket + 2)..closeParen].Trim();
                        result.Add(new ZetlLink(url, children));
                        i = closeParen + 1;
                        textStart = i;
                        continue;
                    }
                }
            }
            // Emphasis: ** bold **, * italic *, ~~ strike ~~.
            else if (c == '*' || c == '~')
            {
                var doubled = i + 1 < end && s[i + 1] == c;
                var kind = c == '*'
                    ? doubled ? "bold" : "italic"
                    : doubled ? "strike" : null;
                if (kind is not null)
                {
                    var width = doubled ? 2 : 1;
                    var close = IndexOf(s, c, width, i + width, end);
                    // A doubled marker opened by a run of three is the canonical
                    // bold+italic emission (`***text***`): the outer pair must close
                    // at the END of the closing run, so the run's remaining single
                    // marker stays inside the children and closes the inner style.
                    if (close > i + width && doubled && i + 2 < end && s[i + 2] == c)
                    {
                        while (close + width < end && s[close + width] == c)
                        {
                            close++;
                        }
                    }

                    if (close > i + width)
                    {
                        FlushText(i);
                        result.Add(new ZetlEmphasis(kind, ParseInlines(s, i + width, close)));
                        i = close + width;
                        textStart = i;
                        continue;
                    }
                }
            }

            i++;
        }

        FlushText(end);
        return result;
    }

    // Find the next run of `width` copies of `delimiter` within [from, end).
    private static int IndexOf(string s, char delimiter, int width, int from, int end)
    {
        for (var i = from; i + width <= end; i++)
        {
            var match = true;
            for (var j = 0; j < width; j++)
            {
                if (s[i + j] != delimiter)
                {
                    match = false;
                    break;
                }
            }

            // A single-char delimiter must not be the start of a doubled one
            // (so the closing `*` of italic is not the first `*` of a later `**`).
            if (match && width == 1 && i + 1 < end && s[i + 1] == delimiter)
            {
                i++;
                continue;
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    // Split slip text into paragraph and list blocks. Consecutive plain lines join a
    // paragraph; consecutive list lines of one kind join a list; a kind change or a
    // plain line ends the run.
    public static IReadOnlyList<ZetlBlock> ParseBlocks(string text)
    {
        var blocks = new List<ZetlBlock>();
        var lines = (text ?? "").ReplaceLineEndings("\n").Split('\n');

        List<IReadOnlyList<ZetlInline>>? paragraph = null;
        List<IReadOnlyList<ZetlInline>>? quote = null;
        string? listKind = null;
        List<ZetlListItem>? items = null;
        var listStart = 1;
        List<string>? code = null;
        var codeLanguage = "";

        void FlushParagraph()
        {
            if (paragraph is { Count: > 0 })
            {
                blocks.Add(new ZetlParagraphBlock(paragraph));
            }

            paragraph = null;
        }

        void FlushQuote()
        {
            if (quote is { Count: > 0 })
            {
                blocks.Add(new ZetlQuoteBlock(quote));
            }

            quote = null;
        }

        void FlushList()
        {
            if (items is { Count: > 0 })
            {
                blocks.Add(new ZetlListBlock(listKind!, items, listStart));
            }

            items = null;
            listKind = null;
            listStart = 1;
        }

        // Close every open inline run before starting a different block kind.
        void FlushAll()
        {
            FlushParagraph();
            FlushQuote();
            FlushList();
        }

        foreach (var line in lines)
        {
            // Inside a fenced code block everything is literal until the closing fence.
            if (code is not null)
            {
                if (IsCodeFence(line, out _))
                {
                    blocks.Add(new ZetlCodeBlock(string.Join("\n", code), codeLanguage));
                    code = null;
                    codeLanguage = "";
                }
                else
                {
                    code.Add(line);
                }

                continue;
            }

            if (IsCodeFence(line, out var language))
            {
                FlushAll();
                code = [];
                codeLanguage = language;
            }
            else if (IsDivider(line))
            {
                FlushAll();
                blocks.Add(new ZetlDividerBlock());
            }
            else if (IsHeading(line, out var level, out var headingContent))
            {
                FlushAll();
                blocks.Add(new ZetlHeadingBlock(level, ParseInlines(headingContent)));
            }
            else if (IsQuote(line, out var quoteContent))
            {
                FlushParagraph();
                FlushList();
                quote ??= [];
                quote.Add(ParseInlines(quoteContent));
            }
            else if (TryClassifyListLine(line, out var kind, out var content, out var isChecked, out var number))
            {
                FlushParagraph();
                FlushQuote();
                if (items is null || listKind != kind)
                {
                    FlushList();
                    listKind = kind;
                    listStart = number;
                    items = [];
                }

                items.Add(new ZetlListItem(ParseInlines(content), isChecked));
            }
            else
            {
                FlushQuote();
                FlushList();
                paragraph ??= [];
                paragraph.Add(ParseInlines(line));
            }
        }

        // An unterminated fence still yields its accumulated content as a code block,
        // matching the inline parser's "unmatched delimiter stays literal" leniency.
        if (code is not null)
        {
            blocks.Add(new ZetlCodeBlock(string.Join("\n", code), codeLanguage));
        }

        FlushAll();
        return blocks;
    }

    // The blocks to render for a whole note. A note whose kind is heading/quote/code/
    // divider renders its entire body as that one block (the body is clean text — the
    // kind is the structure, not inline markup); any other kind parses the body normally
    // so a list item or paragraph still renders its own text. Every block renderer walks
    // this, so the note-kind structure reuses the same heading/quote/code/divider output.
    public static IReadOnlyList<ZetlBlock> BlocksForNote(string? noteKind, string text) =>
        BlocksForNote(noteKind, text, null);

    public static IReadOnlyList<ZetlBlock> BlocksForNote(
        string? noteKind,
        string text,
        IReadOnlyList<ZetlInlineStyleRange>? inlineStyles)
    {
        text = ApplyInlineStyleMarkers(text ?? "", inlineStyles);
        switch (ZetlBlockKinds.Normalize(noteKind))
        {
            case ZetlBlockKinds.Heading:
                return [new ZetlHeadingBlock(2, ParseInlines(text))];
            case ZetlBlockKinds.Quote:
                return [new ZetlQuoteBlock(
                    (text ?? "").ReplaceLineEndings("\n").Split('\n')
                        .Select(line => ParseInlines(line)).ToList())];
            case ZetlBlockKinds.Code:
                return [new ZetlCodeBlock(text ?? "", "")];
            case ZetlBlockKinds.Divider:
                return [new ZetlDividerBlock()];
            default:
                return ParseBlocks(text);
        }
    }

    private sealed record InlineMarker(string Open, string Close, int Priority);

    private static void AddMarker(
        Dictionary<int, List<InlineMarker>> markers,
        int position,
        InlineMarker marker)
    {
        if (!markers.TryGetValue(position, out var list))
        {
            list = [];
            markers[position] = list;
        }

        list.Add(marker);
    }

    private static bool TryGetMarkers(
        ZetlInlineStyleRange range,
        out string open,
        out string close,
        out int priority)
    {
        open = "";
        close = "";
        priority = 0;
        switch (ZetlInlineStyleKinds.Normalize(range.Kind))
        {
            case ZetlInlineStyleKinds.Link when !string.IsNullOrWhiteSpace(range.Href):
                open = "[";
                close = $"]({range.Href!.Trim()})";
                priority = 0;
                return true;
            case ZetlInlineStyleKinds.WikiLink when !string.IsNullOrWhiteSpace(range.TargetSlipId):
                open = $"[[{range.TargetSlipId!.Trim()}|";
                close = "]]";
                priority = 0;
                return true;
            case ZetlInlineStyleKinds.Bold:
                open = close = "**";
                priority = 1;
                return true;
            case ZetlInlineStyleKinds.Italic:
                open = close = "*";
                priority = 2;
                return true;
            case ZetlInlineStyleKinds.Strike:
                open = close = "~~";
                priority = 3;
                return true;
            case ZetlInlineStyleKinds.Code:
                open = close = "`";
                priority = 4;
                return true;
            default:
                return false;
        }
    }

    // A fenced code block opens and closes on a line whose first non-space content is
    // three or more backticks. The opening fence may carry an info string (language
    // hint); a backtick in that string disqualifies it so inline `code` is never a fence.
    private static bool IsCodeFence(string line, out string language)
    {
        language = "";
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return false;
        }

        var ticks = 0;
        while (ticks < trimmed.Length && trimmed[ticks] == '`')
        {
            ticks++;
        }

        var rest = trimmed[ticks..];
        if (rest.Contains('`'))
        {
            return false;
        }

        language = rest.Trim();
        return true;
    }

    // A thematic break: a line of three or more of -, *, or _ (one kind), nothing else.
    private static bool IsDivider(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 3)
        {
            return false;
        }

        var marker = trimmed[0];
        return marker is '-' or '*' or '_' && trimmed.All(ch => ch == marker);
    }

    // An ATX heading: 1-6 leading #, a space, then the heading text.
    private static bool IsHeading(string line, out int level, out string content)
    {
        level = 0;
        content = "";
        var trimmed = line.TrimStart();
        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
        {
            hashes++;
        }

        if (hashes is < 1 or > 6 || hashes >= trimmed.Length || trimmed[hashes] != ' ')
        {
            return false;
        }

        level = hashes;
        content = trimmed[(hashes + 1)..].Trim();
        return true;
    }

    // A blockquote line: "> " (or a bare ">"), with one optional space after the marker.
    private static bool IsQuote(string line, out string content)
    {
        content = "";
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('>'))
        {
            return false;
        }

        var rest = trimmed[1..];
        content = rest.StartsWith(' ') ? rest[1..] : rest;
        return true;
    }

    // True when the slip text holds any block structure beyond paragraphs and lists
    // (a heading, blockquote, fenced code block, or divider). The Markdown export emits
    // such slips as their own blocks rather than wrapping them under a bucket marker.
    public static bool ContainsBlockStructure(string text) =>
        ParseBlocks(text).Any(block =>
            block is ZetlHeadingBlock or ZetlQuoteBlock or ZetlCodeBlock or ZetlDividerBlock);

    // Convert "# heading" lines to a bold line so a softened sub-heading never enters
    // the exported Markdown outline. Fence-aware: lines inside a ``` block are left
    // untouched (a "#" there is literal code, not a heading).
    public static IReadOnlyList<string> SoftenHeadingsForMarkdown(IEnumerable<string> lines)
    {
        var result = new List<string>();
        var inCode = false;
        foreach (var line in lines)
        {
            if (IsCodeFence(line, out _))
            {
                inCode = !inCode;
                result.Add(line);
            }
            else if (!inCode && IsHeading(line, out _, out var content))
            {
                result.Add(content.Length == 0 ? "" : $"**{content}**");
            }
            else
            {
                result.Add(line);
            }
        }

        return result;
    }

    private static bool TryClassifyListLine(
        string line,
        out string kind,
        out string content,
        out bool isChecked,
        out int number)
    {
        kind = "";
        content = "";
        isChecked = false;
        number = 1;
        var trimmed = line.TrimStart();

        // Task: "- [ ] …" or "- [x] …" (checked first so the bullet rule does not win).
        if (trimmed.Length >= 5
            && (trimmed[0] == '-' || trimmed[0] == '*')
            && trimmed[1] == ' '
            && trimmed[2] == '['
            && trimmed[4] == ']'
            && (trimmed[3] is ' ' or 'x' or 'X')
            && (trimmed.Length == 5 || trimmed[5] == ' '))
        {
            kind = "task";
            isChecked = trimmed[3] is 'x' or 'X';
            content = trimmed.Length > 6 ? trimmed[6..] : "";
            return true;
        }

        // Bullet: "- …" or "* …".
        if (trimmed.StartsWith("- ", StringComparison.Ordinal)
            || trimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            kind = "bullet";
            content = trimmed[2..];
            return true;
        }

        // Ordered: "12. …".
        var dot = trimmed.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0 && trimmed[..dot].All(char.IsAsciiDigit))
        {
            kind = "ordered";
            content = trimmed[(dot + 2)..];
            number = int.TryParse(trimmed[..dot], out var parsed) ? parsed : 1;
            return true;
        }

        return false;
    }

    // Render a slip's blocks to the inner HTML of its list item: paragraphs as text
    // with line breaks, lists as <ul>/<ol> (task lists use checkbox inputs).
    public static string BlocksToHtml(string text, Func<string, bool>? isResolved = null) => BlocksToHtml(ParseBlocks(text), isResolved);

    public static string BlocksToHtml(IReadOnlyList<ZetlBlock> blocks, Func<string, bool>? isResolved = null)
    {
        var builder = new StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ZetlParagraphBlock paragraph:
                    builder.Append(string.Join("<br />", paragraph.Lines.Select(lines => InlinesToHtml(lines, isResolved))));
                    break;
                case ZetlHeadingBlock heading:
                    // Softened: a bold, slightly larger lead — never a real <h*> — so it
                    // stays out of the document outline and any future table of contents.
                    var headingSize = heading.Level <= 1 ? "1.15em" : heading.Level == 2 ? "1.05em" : "1em";
                    builder.Append($"<p style=\"font-weight:700;font-size:{headingSize};margin:0.5em 0 0.2em\">")
                        .Append(InlinesToHtml(heading.Inlines, isResolved))
                        .Append("</p>");
                    break;
                case ZetlQuoteBlock quote:
                    builder.Append("<blockquote>")
                        .Append(string.Join("<br />", quote.Lines.Select(lines => InlinesToHtml(lines, isResolved))))
                        .Append("</blockquote>");
                    break;
                case ZetlCodeBlock code:
                    builder.Append("<pre><code>").Append(Escape(code.Text)).Append("</code></pre>");
                    break;
                case ZetlDividerBlock:
                    builder.Append("<hr />");
                    break;
                case ZetlListBlock { Kind: "ordered" } ordered:
                    builder.Append(ordered.Start > 1 ? $"<ol start=\"{ordered.Start}\">" : "<ol>");
                    foreach (var item in ordered.Items)
                    {
                        builder.Append("<li>").Append(InlinesToHtml(item.Inlines, isResolved)).Append("</li>");
                    }
                    builder.Append("</ol>");
                    break;
                case ZetlListBlock { Kind: "task" } task:
                    builder.Append("<ul style=\"list-style:none;padding-left:1.1em\">");
                    foreach (var item in task.Items)
                    {
                        builder.Append("<li>")
                            .Append(TaskCheckboxHtml(item.Checked))
                            .Append(InlinesToHtml(item.Inlines, isResolved))
                            .Append("</li>");
                    }
                    builder.Append("</ul>");
                    break;
                case ZetlListBlock bullet:
                    builder.Append("<ul>");
                    foreach (var item in bullet.Items)
                    {
                        builder.Append("<li>").Append(InlinesToHtml(item.Inlines, isResolved)).Append("</li>");
                    }
                    builder.Append("</ul>");
                    break;
            }
        }

        return builder.ToString();
    }

    public static string InlinesToHtml(IReadOnlyList<ZetlInline> inlines) => InlinesToHtml(inlines, null);

    public static string InlinesToHtml(IReadOnlyList<ZetlInline> inlines, Func<string, bool>? isResolved)
    {
        var builder = new StringBuilder();
        foreach (var inline in inlines)
        {
            AppendHtml(builder, inline, isResolved);
        }

        return builder.ToString();
    }

    public static string InlinesToHtml(string text, Func<string, bool>? isResolved = null) => InlinesToHtml(ParseInlines(text), isResolved);

    public static string TaskCheckboxHtml(bool isChecked) =>
        isChecked
            ? "<input type=\"checkbox\" checked /> "
            : "<input type=\"checkbox\" /> ";

    private static void AppendHtml(StringBuilder builder, ZetlInline inline, Func<string, bool>? isResolved)
    {
        switch (inline)
        {
            case ZetlTextRun run:
                builder.Append(Escape(run.Text));
                break;
            case ZetlCodeRun code:
                builder.Append("<code>").Append(Escape(code.Text)).Append("</code>");
                break;
            case ZetlEmphasis emphasis:
                var tag = emphasis.Kind switch
                {
                    "bold" => "strong",
                    "italic" => "em",
                    "strike" => "del",
                    _ => "span"
                };
                builder.Append('<').Append(tag).Append('>');
                foreach (var child in emphasis.Children)
                {
                    AppendHtml(builder, child, isResolved);
                }
                builder.Append("</").Append(tag).Append('>');
                break;
            case ZetlLink link:
                builder.Append("<a href=\"").Append(EscapeAttribute(link.Url)).Append("\">");
                foreach (var child in link.Children)
                {
                    AppendHtml(builder, child, isResolved);
                }
                builder.Append("</a>");
                break;
            case ZetlWikiLink wiki:
                var resolved = isResolved?.Invoke(wiki.TargetId) ?? false;
                if (resolved)
                {
                    builder.Append("<a href=\"#").Append(EscapeAttribute(wiki.TargetId)).Append("\">")
                        .Append(Escape(wiki.CachedTitle))
                        .Append("</a>");
                }
                else
                {
                    builder.Append("<span class=\"kastn-unresolved-link\" style=\"color:#666;text-decoration:underline;cursor:help;\" title=\"Slip not found\">")
                        .Append(Escape(wiki.CachedTitle))
                        .Append("</span>");
                }
                break;
        }
    }

    public static string ResolveWikiLinksInNote(string text, Func<string, bool> isResolved)
    {
        var blocks = ParseBlocks(text);
        return BlocksToMarkdown(blocks, isResolved);
    }

    public static string BlocksToMarkdown(IReadOnlyList<ZetlBlock> blocks, Func<string, bool> isResolved)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (i > 0)
            {
                builder.AppendLine();
            }
            switch (block)
            {
                case ZetlParagraphBlock p:
                    for (var line = 0; line < p.Lines.Count; line++)
                    {
                        if (line > 0) builder.AppendLine();
                        builder.Append(InlinesToMarkdown(p.Lines[line], isResolved));
                    }
                    builder.AppendLine();
                    break;
                case ZetlListBlock list:
                    var number = list.Start;
                    foreach (var item in list.Items)
                    {
                        var marker = list.Kind switch
                        {
                            "ordered" => $"{number++}. ",
                            "task" => item.Checked ? "- [x] " : "- [ ] ",
                            _ => "- "
                        };
                        builder.Append(marker).Append(InlinesToMarkdown(item.Inlines, isResolved)).AppendLine();
                    }
                    break;
                case ZetlHeadingBlock h:
                    builder.Append(new string('#', h.Level)).Append(' ').Append(InlinesToMarkdown(h.Inlines, isResolved)).AppendLine();
                    break;
                case ZetlQuoteBlock q:
                    for (var line = 0; line < q.Lines.Count; line++)
                    {
                        if (line > 0) builder.AppendLine();
                        builder.Append("> ").Append(InlinesToMarkdown(q.Lines[line], isResolved));
                    }
                    builder.AppendLine();
                    break;
                case ZetlCodeBlock code:
                    builder.Append("```").AppendLine(code.Language);
                    builder.Append(code.Text);
                    if (!code.Text.EndsWith('\n')) builder.AppendLine();
                    builder.AppendLine("```");
                    break;
                case ZetlDividerBlock:
                    builder.AppendLine("---");
                    break;
            }
        }
        return builder.ToString().TrimEnd('\r', '\n');
    }

    public static string InlinesToMarkdown(IReadOnlyList<ZetlInline> inlines, Func<string, bool> isResolved)
    {
        var builder = new StringBuilder();
        AppendMarkdown(builder, inlines, isResolved);
        return builder.ToString();
    }

    private static void AppendMarkdown(StringBuilder builder, IReadOnlyList<ZetlInline> inlines, Func<string, bool> isResolved)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case ZetlTextRun run:
                    builder.Append(run.Text);
                    break;
                case ZetlCodeRun code:
                    builder.Append('`').Append(code.Text).Append('`');
                    break;
                case ZetlEmphasis emphasis:
                    var marker = emphasis.Kind switch
                    {
                        "bold" => "**",
                        "italic" => "*",
                        "strike" => "~~",
                        _ => ""
                    };
                    builder.Append(marker);
                    AppendMarkdown(builder, emphasis.Children, isResolved);
                    builder.Append(marker);
                    break;
                case ZetlLink link:
                    builder.Append('[');
                    AppendMarkdown(builder, link.Children, isResolved);
                    builder.Append("](").Append(link.Url).Append(')');
                    break;
                case ZetlWikiLink wiki:
                    if (isResolved(wiki.TargetId))
                    {
                        builder.Append('[').Append(wiki.CachedTitle).Append("](#").Append(wiki.TargetId).Append(')');
                    }
                    else
                    {
                        builder.Append(wiki.CachedTitle);
                    }
                    break;
            }
        }
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string EscapeAttribute(string text) =>
        Escape(text).Replace("\"", "&quot;");
}
