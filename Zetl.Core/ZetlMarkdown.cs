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
