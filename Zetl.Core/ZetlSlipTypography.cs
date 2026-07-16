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
