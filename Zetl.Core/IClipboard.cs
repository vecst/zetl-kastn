namespace ZETL;

/// <summary>
/// The system clipboard. Platform backends expose text and normalized image
/// snapshots, exact backup/restore when available, plus a token used to detect
/// content changes.
/// </summary>
internal interface IClipboard
{
    /// <summary>The clipboard's Unicode text, or null if it has none / is unavailable.</summary>
    string? TryGetText();

    /// <summary>
    /// The clipboard's HTML fragment, or null when no HTML representation is
    /// available. This is the rich companion to <see cref="TryGetText"/>.
    /// </summary>
    string? TryGetHtml();

    /// <summary>
    /// A narrowly allowlisted native rich-content bundle for replaying source
    /// application data without an interchange-format conversion. Null means
    /// the clipboard has no supported self-contained native representation.
    /// </summary>
    IReadOnlyList<ZetlClipboardFormatData>? TryGetReplayFormats();

    /// <summary>A normalized PNG snapshot, or null when no clipboard image is available.</summary>
    ZetlClipboardImage? TryGetImage();

    /// <summary>
    /// Replace the clipboard text. Returns true when the write succeeded, false
    /// when it could not be completed (so callers that must not act on a stale
    /// clipboard — e.g. replay paste — can bail).
    /// </summary>
    bool SetText(string text);

    /// <summary>
    /// Replace the clipboard with rich text plus a plain-text fallback. Backends
    /// that cannot carry rich formats should write the fallback text.
    /// </summary>
    bool SetRichText(string plainText, string html);

    /// <summary>Replace the clipboard with a normalized PNG image.</summary>
    bool SetImage(ZetlClipboardImage image);

    /// <summary>
    /// Capture the complete clipboard for a later non-lossy restore. Callers
    /// must not replace the clipboard when the returned backup is incomplete.
    /// </summary>
    ZetlClipboardBackup CaptureBackup();

    /// <summary>Restore a complete backup captured by this backend.</summary>
    bool RestoreBackup(ZetlClipboardBackup backup);

    /// <summary>
    /// A token that changes whenever the clipboard content changes, used to
    /// detect "did the clipboard update after I copied". Only compared for
    /// equality, so any change-detecting value works (a content hash is fine).
    /// </summary>
    uint GetChangeToken();
}

internal sealed record ZetlClipboardImage(byte[] PngBytes, int Width, int Height);

/// <summary>An opaque clipboard format payload. Contents must never be logged.</summary>
internal sealed record ZetlClipboardFormatData(
    uint Format,
    byte[] Data,
    string? RegisteredName = null);

/// <summary>
/// A complete clipboard backup. Windows uses RawFormats (an empty list means an
/// exactly empty clipboard); portable backends use the typed fallback fields.
/// </summary>
internal sealed record ZetlClipboardBackup(
    IReadOnlyList<ZetlClipboardFormatData>? RawFormats,
    string? Text,
    string? Html,
    ZetlClipboardImage? Image,
    bool IsComplete,
    string? FailureReason)
{
    public static ZetlClipboardBackup FromRaw(IReadOnlyList<ZetlClipboardFormatData> formats) =>
        new(formats, null, null, null, true, null);

    public static ZetlClipboardBackup FromPortable(
        string? text,
        string? html,
        ZetlClipboardImage? image) =>
        new(null, text, html, image, true, null);

    public static ZetlClipboardBackup Incomplete(string reason) =>
        new(null, null, null, null, false, reason);
}

/// <summary>
/// Resolves an explicitly copied HTTP(S) URL into a normalized image. The
/// platform implementation owns networking and image decoding so the portable
/// shortcut runtime remains UI- and codec-agnostic.
/// </summary>
internal interface IImageUrlResolver
{
    Task<ZetlResolvedImageUrl?> TryResolveAsync(string text);
}

internal sealed record ZetlResolvedImageUrl(
    ZetlClipboardImage Image,
    string SourceUrl);
