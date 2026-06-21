namespace ZETL;

/// <summary>
/// The system clipboard. Platform backends expose text and normalized image
/// snapshots plus a token used to detect content changes.
/// </summary>
internal interface IClipboard
{
    /// <summary>The clipboard's Unicode text, or null if it has none / is unavailable.</summary>
    string? TryGetText();

    /// <summary>A normalized PNG snapshot, or null when no clipboard image is available.</summary>
    ZetlClipboardImage? TryGetImage();

    /// <summary>
    /// Replace the clipboard text. Returns true when the write succeeded, false
    /// when it could not be completed (so callers that must not act on a stale
    /// clipboard — e.g. replay paste — can bail).
    /// </summary>
    bool SetText(string text);

    /// <summary>Replace the clipboard with a normalized PNG image.</summary>
    bool SetImage(ZetlClipboardImage image);

    /// <summary>
    /// A token that changes whenever the clipboard content changes, used to
    /// detect "did the clipboard update after I copied". Only compared for
    /// equality, so any change-detecting value works (a content hash is fine).
    /// </summary>
    uint GetChangeToken();
}

internal sealed record ZetlClipboardImage(byte[] PngBytes, int Width, int Height);
