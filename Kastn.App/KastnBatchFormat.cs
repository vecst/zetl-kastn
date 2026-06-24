namespace KASTN;

// Pure helpers for batch slip formatting (UI roadmap Phase 2): list-marker
// rewriting and strikethrough toggling. Kept free of UI state so they can be unit
// tested. (Ordered-list numbering is computed at render time in ZetlViewRenderer.)
internal static class KastnBatchFormat
{
    // Replace a line's leading list marker (bullet / checkbox / ordered), if any, so
    // applying a list kind never stacks markers ("- - [ ] x").
    public static string StripLeadingListMarker(string line)
    {
        var indent = line.Length - line.TrimStart().Length;
        var body = line[indent..];

        // Checkbox: "- [ ] " / "- [x] ".
        if (body.StartsWith("- [", StringComparison.Ordinal)
            && body.Length >= 6 && body[4] == ']' && body[5] == ' ')
        {
            return line[..indent] + body[6..];
        }

        // Bullet: "- ", "* ", "+ ".
        if (body.Length >= 2 && body[0] is '-' or '*' or '+' && body[1] == ' ')
        {
            return line[..indent] + body[2..];
        }

        // Ordered: "N. " / "N) ".
        var digits = 0;
        while (digits < body.Length && char.IsDigit(body[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits + 1 < body.Length && body[digits] is '.' or ')' && body[digits + 1] == ' ')
        {
            return line[..indent] + body[(digits + 2)..];
        }

        return line;
    }

    // Make a whole slip one list item: the marker prefixes the first line (its old
    // marker stripped); the rest of a multi-line slip is left untouched.
    public static string ApplyLineMarker(string text, string marker)
    {
        var newlineIndex = text.IndexOf('\n');
        var firstLine = newlineIndex < 0 ? text : text[..newlineIndex];
        var rest = newlineIndex < 0 ? "" : text[newlineIndex..];
        return marker + StripLeadingListMarker(firstLine) + rest;
    }

    public static string ToggleStrike(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length >= 4
            && trimmed.StartsWith("~~", StringComparison.Ordinal)
            && trimmed.EndsWith("~~", StringComparison.Ordinal))
        {
            return trimmed[2..^2];
        }

        return $"~~{text}~~";
    }
}
