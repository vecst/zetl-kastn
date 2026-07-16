namespace ZETL;

/// <summary>
/// Central policy for authored links that can become active in a reader or export.
/// Project files are user-editable and may predate validation, so every renderer must
/// apply this policy instead of trusting persisted or Markdown-provided targets.
/// </summary>
internal static class ZetlLinkSafety
{
    public static bool TryNormalizeTarget(string? value, out string target)
    {
        target = value?.Trim() ?? "";
        if (target.Length == 0 || target.Any(char.IsControl))
        {
            target = "";
            return false;
        }

        if (target[0] == '#')
        {
            if (target.Length > 1 && !target.Any(char.IsWhiteSpace))
            {
                return true;
            }

            target = "";
            return false;
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            target = "";
            return false;
        }

        var safe = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);
        if (!safe
            || (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                && string.IsNullOrWhiteSpace(uri.Host))
        {
            target = "";
            return false;
        }

        return true;
    }
}
