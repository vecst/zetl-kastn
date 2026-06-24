using System;
using System.Linq;

namespace ZETL;

// Derives a slip's type from its content where the content makes the type
// unambiguous. Picture stays stored (it owns a binary asset); URL-ness is purely a
// function of the text, so it is derived — no stored field, no migration, and the
// type always tracks the current text.
internal static class ZetlSlipClassifier
{
    // True when the slip's first non-empty line is a bare absolute http/https URL.
    // That covers both "I copied a link" and the common "link, then a new line with a
    // note about it" capture; any lines after the URL are treated as the user's note.
    // Conservative on the URL line itself: a scheme-less host ("example.com") or prose
    // that merely contains a link ("see https://x.com here") stays text.
    public static bool LooksLikeUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            return !trimmed.Any(char.IsWhiteSpace)
                && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        return false;
    }
}
