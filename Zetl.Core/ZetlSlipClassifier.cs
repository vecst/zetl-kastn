using System.Text.RegularExpressions;

namespace ZETL;

// Derives a slip's type from its content where the content makes the type
// unambiguous. Picture stays stored (it owns a binary asset); URL-ness is purely a
// function of the text, so it is derived — no stored field, no migration, and the
// type always tracks the current text.
internal static class ZetlSlipClassifier
{
    // Match an http/https URL anywhere in the text (scheme + at least one more char).
    private static readonly Regex UrlPattern =
        new(@"https?://\S", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A note is a link if it contains an http/https URL *anywhere* — paste a link and
    // annotate it however you like (before it, after it, mid-sentence) and it still
    // files under links. Conservative only in requiring an explicit web scheme, so a
    // scheme-less host ("example.com") or a non-web scheme ("ftp://") stays text.
    public static bool LooksLikeUrl(string? text) =>
        !string.IsNullOrWhiteSpace(text) && UrlPattern.IsMatch(text);
}
