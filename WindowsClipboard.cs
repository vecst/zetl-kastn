using System.Runtime.InteropServices;

namespace ZETL;

/// <summary>
/// Windows <see cref="IClipboard"/>: the WinForms clipboard (tolerating the
/// transient failures Windows raises when another process holds the clipboard
/// open) plus GetClipboardSequenceNumber for change detection.
/// </summary>
internal sealed class WindowsClipboard : IClipboard
{
    public string? TryGetText()
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

    public bool SetText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
        catch (ThreadStateException)
        {
            return false;
        }
    }

    public uint GetChangeToken()
    {
        return GetClipboardSequenceNumber();
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
