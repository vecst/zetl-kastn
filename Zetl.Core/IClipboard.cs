namespace ZETL;

/// <summary>
/// The system clipboard. Windows uses the WinForms clipboard plus
/// GetClipboardSequenceNumber; a Linux backend will shell out to
/// wl-clipboard/xclip and hash the content for the change token.
/// </summary>
internal interface IClipboard
{
    /// <summary>The clipboard's Unicode text, or null if it has none / is unavailable.</summary>
    string? TryGetText();

    /// <summary>
    /// Replace the clipboard text. Returns true when the write succeeded, false
    /// when it could not be completed (so callers that must not act on a stale
    /// clipboard — e.g. replay paste — can bail).
    /// </summary>
    bool SetText(string text);

    /// <summary>
    /// A token that changes whenever the clipboard content changes, used to
    /// detect "did the clipboard update after I copied". Only compared for
    /// equality, so any change-detecting value works (a content hash is fine).
    /// </summary>
    uint GetChangeToken();
}
