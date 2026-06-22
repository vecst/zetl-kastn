using System.Text;

namespace ZETL;

// The hand-rolled Markdown layer for slip text (UI roadmap item 7). Intentionally a
// small subset — inline emphasis, code, and links — parsed into one AST that every
// renderer walks: HTML here, and MigraDoc/Avalonia inlines in the head. Unmatched
// delimiters fall back to literal text, so plain text is always valid input and the
// literal views (Formatted/Plain/TSV) can keep ignoring it and render verbatim.

internal abstract record ZetlInline;

internal sealed record ZetlTextRun(string Text) : ZetlInline;

internal sealed record ZetlCodeRun(string Text) : ZetlInline;

// Kind is "bold", "italic", or "strike".
internal sealed record ZetlEmphasis(string Kind, IReadOnlyList<ZetlInline> Children) : ZetlInline;

internal sealed record ZetlLink(string Url, IReadOnlyList<ZetlInline> Children) : ZetlInline;

// Block-level structure within one slip: consecutive plain lines form a paragraph,
// consecutive same-kind list lines form a list. Lists are flat (no nesting inside a
// slip — bucket nesting is the renderer's job).
internal abstract record ZetlBlock;

internal sealed record ZetlParagraphBlock(IReadOnlyList<IReadOnlyList<ZetlInline>> Lines) : ZetlBlock;

internal sealed record ZetlListItem(IReadOnlyList<ZetlInline> Inlines, bool Checked);

// Kind is "bullet", "ordered", or "task".
internal sealed record ZetlListBlock(string Kind, IReadOnlyList<ZetlListItem> Items) : ZetlBlock;

internal static class ZetlMarkdown
{
    public static IReadOnlyList<ZetlInline> ParseInlines(string text) =>
        ParseInlines(text ?? "", 0, (text ?? "").Length);

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
        string? listKind = null;
        List<ZetlListItem>? items = null;

        void FlushParagraph()
        {
            if (paragraph is { Count: > 0 })
            {
                blocks.Add(new ZetlParagraphBlock(paragraph));
            }

            paragraph = null;
        }

        void FlushList()
        {
            if (items is { Count: > 0 })
            {
                blocks.Add(new ZetlListBlock(listKind!, items));
            }

            items = null;
            listKind = null;
        }

        foreach (var line in lines)
        {
            if (TryClassifyListLine(line, out var kind, out var content, out var isChecked))
            {
                FlushParagraph();
                if (items is null || listKind != kind)
                {
                    FlushList();
                    listKind = kind;
                    items = [];
                }

                items.Add(new ZetlListItem(ParseInlines(content), isChecked));
            }
            else
            {
                FlushList();
                paragraph ??= [];
                paragraph.Add(ParseInlines(line));
            }
        }

        FlushParagraph();
        FlushList();
        return blocks;
    }

    private static bool TryClassifyListLine(
        string line,
        out string kind,
        out string content,
        out bool isChecked)
    {
        kind = "";
        content = "";
        isChecked = false;
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
            return true;
        }

        return false;
    }

    // Render a slip's blocks to the inner HTML of its list item: paragraphs as text
    // with line breaks, lists as <ul>/<ol> (task lists use checkbox glyphs).
    public static string BlocksToHtml(string text)
    {
        var builder = new StringBuilder();
        foreach (var block in ParseBlocks(text))
        {
            switch (block)
            {
                case ZetlParagraphBlock paragraph:
                    builder.Append(string.Join("<br />", paragraph.Lines.Select(InlinesToHtml)));
                    break;
                case ZetlListBlock { Kind: "ordered" } ordered:
                    builder.Append("<ol>");
                    foreach (var item in ordered.Items)
                    {
                        builder.Append("<li>").Append(InlinesToHtml(item.Inlines)).Append("</li>");
                    }
                    builder.Append("</ol>");
                    break;
                case ZetlListBlock { Kind: "task" } task:
                    builder.Append("<ul style=\"list-style:none;padding-left:1.1em\">");
                    foreach (var item in task.Items)
                    {
                        builder.Append("<li>")
                            .Append(item.Checked ? "☑ " : "☐ ")
                            .Append(InlinesToHtml(item.Inlines))
                            .Append("</li>");
                    }
                    builder.Append("</ul>");
                    break;
                case ZetlListBlock bullet:
                    builder.Append("<ul>");
                    foreach (var item in bullet.Items)
                    {
                        builder.Append("<li>").Append(InlinesToHtml(item.Inlines)).Append("</li>");
                    }
                    builder.Append("</ul>");
                    break;
            }
        }

        return builder.ToString();
    }

    public static string InlinesToHtml(IReadOnlyList<ZetlInline> inlines)
    {
        var builder = new StringBuilder();
        foreach (var inline in inlines)
        {
            AppendHtml(builder, inline);
        }

        return builder.ToString();
    }

    public static string InlinesToHtml(string text) => InlinesToHtml(ParseInlines(text));

    private static void AppendHtml(StringBuilder builder, ZetlInline inline)
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
                    AppendHtml(builder, child);
                }
                builder.Append("</").Append(tag).Append('>');
                break;
            case ZetlLink link:
                builder.Append("<a href=\"").Append(EscapeAttribute(link.Url)).Append("\">");
                foreach (var child in link.Children)
                {
                    AppendHtml(builder, child);
                }
                builder.Append("</a>");
                break;
        }
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string EscapeAttribute(string text) =>
        Escape(text).Replace("\"", "&quot;");
}
