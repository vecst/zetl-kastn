namespace ZETL;

/// <summary>
/// Clipboard formats that link back to the copied document instead of holding
/// content. Asking for one changes the source: LibreOffice Writer answers the
/// "Link" format (DDE) by putting a bookmark around the copied text, which shows
/// as [ ] marks that can't be deleted on their own. Zetl never reads them, so a
/// clipboard backup leaves them out; they carry nothing a restore needs.
/// </summary>
internal static class ZetlClipboardLinkFormats
{
    // Windows registered names: DDE links and OLE link sources.
    private static readonly HashSet<string> WindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Link", "ObjectLink", "OwnerLink", "Link Source", "Link Source Descriptor"
    };

    /// <summary>A Windows registered format name or a Linux MIME type.</summary>
    public static bool IsLink(string? name) =>
        name is not null
        && (WindowsNames.Contains(name)
            // LibreOffice's X11 name for the same DDE link, with parameters.
            || name.StartsWith("application/x-openoffice-link", StringComparison.OrdinalIgnoreCase));
}
