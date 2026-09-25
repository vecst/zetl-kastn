namespace ZETL;

// HTML escaping shared by the Markdown and view renderers.
internal static class ZetlHtml
{
    public static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static string EscapeAttribute(string text) =>
        Escape(text).Replace("\"", "&quot;");
}
