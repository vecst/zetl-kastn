namespace KASTN;

// Pure helpers for batch slip formatting (UI roadmap Phase 2): strikethrough
// toggling. Kept free of UI state so it can be unit tested. (List-item kind is now a
// per-note property, not body markup; ordered numbering is computed at render time.)
internal static class KastnBatchFormat
{
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
