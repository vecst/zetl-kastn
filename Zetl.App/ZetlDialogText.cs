namespace ZETL;

internal static class ZetlDialogText
{
    // A multi-line dialog field (bucket names, starting text) as trimmed,
    // non-empty lines, tolerant of any newline convention.
    public static IReadOnlyList<string> SplitLines(string? text)
    {
        return (text ?? "")
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }
}
