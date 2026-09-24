namespace ZETL;

/// <summary>
/// Normalizes the portable, whole-slip typography values stored in project JSON.
/// Empty values mean "inherit the active theme"; keeping that representation in
/// one place prevents UI and renderer-specific values from leaking into storage.
/// </summary>
internal static class ZetlSlipTypography
{
    public const int MaximumFontFamilyLength = 128;
    public const int MinimumFontSize = 8;
    public const int MaximumFontSize = 96;

    public static string NormalizeFontFamily(string? value)
    {
        var normalized = value?.Trim() ?? "";
        if (normalized.Length > MaximumFontFamilyLength)
        {
            normalized = normalized[..MaximumFontFamilyLength].TrimEnd();
        }

        return normalized;
    }

    public static int NormalizeFontSize(int value) =>
        value <= 0 ? 0 : Math.Clamp(value, MinimumFontSize, MaximumFontSize);

    public static string NormalizeTextColor(string? value)
    {
        var normalized = value?.Trim() ?? "";
        if (normalized.Length != 7
            || normalized[0] != '#'
            || !normalized.AsSpan(1).ContainsOnlyHexDigits())
        {
            return "";
        }

        return normalized.ToUpperInvariant();
    }

    // The channels of a text color, or null when the slip inherits the theme
    // color. Validates through NormalizeTextColor, so any stored value is safe.
    public static (byte Red, byte Green, byte Blue)? TextColorRgb(string? value)
    {
        var normalized = NormalizeTextColor(value);
        if (normalized.Length == 0)
        {
            return null;
        }

        var rgb = Convert.ToUInt32(normalized[1..], 16);
        return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static bool ContainsOnlyHexDigits(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
