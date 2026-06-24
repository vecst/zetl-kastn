using System;
using System.Linq;

namespace ZETL;

// Derives a slip's type from its content where the content makes the type
// unambiguous. Picture stays stored (it owns a binary asset); URL-ness is purely a
// function of the text, so it is derived — no stored field, no migration, and the
// type always tracks the current text.
internal static class ZetlSlipClassifier
{
    // True when the whole captured text is a single absolute http/https URL — the
    // "I copied a link" case, not prose that merely contains a link. Conservative on
    // purpose (a scheme-less host like "example.com" is left as text).
    public static bool LooksLikeUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
