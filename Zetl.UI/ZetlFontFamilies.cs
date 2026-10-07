using Avalonia.Media;

namespace ZETL;

/// <summary>
/// Turns a theme or slip font name (or a comma-separated list) into the family
/// Avalonia should render. On Linux, fontconfig answers every name with some
/// installed font, so an uninstalled "Consolas" quietly becomes a proportional
/// sans and the first name in a fallback list always wins. This picks the first
/// family that is really installed, honors generic names, and sends known
/// monospace and serif families that are missing to their generic kind.
/// Installed names resolve exactly as before, so Windows is unaffected.
/// </summary>
internal static class ZetlFontFamilies
{
    private static readonly HashSet<string> GenericFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "monospace", "sans-serif", "serif", "sans"
    };

    private static readonly Dictionary<string, string> KnownKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cascadia Mono"] = "monospace",
        ["Cascadia Code"] = "monospace",
        ["Consolas"] = "monospace",
        ["Courier New"] = "monospace",
        ["Lucida Console"] = "monospace",
        ["Georgia"] = "serif",
        ["Times New Roman"] = "serif",
        ["Cambria"] = "serif",
    };

    private static HashSet<string>? installed;

    public static FontFamily Resolve(string families) => new(ResolveName(families));

    internal static string ResolveName(string families) =>
        ResolveName(families, InstalledFamilies());

    internal static string ResolveName(string families, IReadOnlySet<string> installedFamilies)
    {
        // Without a font list to check against (a headless renderer), the
        // authored names are the best answer.
        if (installedFamilies.Count == 0) return families;

        var names = families.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in names)
        {
            // Installed families, generic names, and bundled resources
            // ("fonts:Inter#Inter") all resolve to what they say.
            if (installedFamilies.Contains(name) || GenericFamilies.Contains(name) || name.Contains(':'))
            {
                return name;
            }
        }

        foreach (var name in names)
        {
            if (KnownKinds.TryGetValue(name, out var kind)) return kind;
        }

        return families;
    }

    private static IReadOnlySet<string> InstalledFamilies()
    {
        if (installed is { } cached) return cached;
        try
        {
            cached = FontManager.Current.SystemFonts
                .Select(family => family.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            // No font manager yet (tests without a platform): resolve nothing.
            return new HashSet<string>();
        }

        return installed = cached;
    }
}
