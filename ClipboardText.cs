using System.Runtime.InteropServices;

namespace ZETL;

/// <summary>
/// Clipboard text access that tolerates the transient failures Windows
/// raises when another process holds the clipboard open.
/// </summary>
internal static class ClipboardText
{
    public static string? TryGet()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : null;
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (ThreadStateException)
        {
            return null;
        }
    }

    public static void Set(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
        }
        catch (ThreadStateException)
        {
        }
    }
}
