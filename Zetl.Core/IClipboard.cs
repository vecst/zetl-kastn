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
    /// True when the app that put this content on the clipboard marked it as
    /// private (a password manager, typically), asking clipboard monitors and
    /// history not to read or keep it. Zetl then captures nothing.
    /// </summary>
    bool IsMarkedPrivate() => false;

    /// <summary>
    /// Capture every format used by Zetl from one clipboard generation. Native
    /// backends should override this with one platform read transaction. The
    /// compatibility implementation retries when the change token advances
    /// between individual format reads and never returns a torn snapshot.
    /// </summary>
    ZetlClipboardCaptureSnapshot? TryCaptureContent()
    {
        const int maxAttempts = 3;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var before = GetChangeToken();
            if (IsMarkedPrivate())
            {
                return ZetlClipboardCaptureSnapshot.PrivateContent(before);
            }

            var text = TryGetText();
            var html = TryGetHtml();
            var replayFormats = TryGetReplayFormats();
            var image = TryGetImage();
            var after = GetChangeToken();
            if (before == after)
            {
                return new ZetlClipboardCaptureSnapshot(
                    after,
                    text,
                    html,
                    replayFormats,
                    image);
            }
        }

        return null;
    }

    /// <summary>
    /// Replace the clipboard text. Returns true when the write succeeded, false
    /// when it could not be completed (so callers that must not act on a stale
    /// clipboard — e.g. replay paste — can bail).
    /// </summary>
    bool SetText(string text);

    /// <summary>
    /// Structured replacement result. Portable test/platform implementations
    /// may rely on this compatibility default; native backends should override
    /// it with transactional staging and rollback details.
    /// </summary>
    ZetlClipboardWriteResult ReplaceText(string text) =>
        ZetlClipboardWriteResult.FromLegacy(SetText(text));

    /// <summary>
    /// Replace the clipboard with rich text plus a plain-text fallback. Backends
    /// that cannot carry rich formats should write the fallback text.
    /// </summary>
    bool SetRichText(string plainText, string html);

    ZetlClipboardWriteResult ReplaceRichText(string plainText, string html) =>
        ZetlClipboardWriteResult.FromLegacy(SetRichText(plainText, html));

    /// <summary>Replace the clipboard with a normalized PNG image.</summary>
    bool SetImage(ZetlClipboardImage image);

    ZetlClipboardWriteResult ReplaceImage(ZetlClipboardImage image) =>
        ZetlClipboardWriteResult.FromLegacy(SetImage(image));

    /// <summary>
    /// Capture the complete clipboard for a later non-lossy restore. Callers
    /// must not replace the clipboard when the returned backup is incomplete.
    /// </summary>
    ZetlClipboardBackup CaptureBackup();

    /// <summary>Restore a complete backup captured by this backend.</summary>
    bool RestoreBackup(ZetlClipboardBackup backup);

    ZetlClipboardWriteResult ReplaceWithBackup(ZetlClipboardBackup backup) =>
        ZetlClipboardWriteResult.FromLegacy(RestoreBackup(backup));

    /// <summary>
    /// A token that changes whenever the clipboard content changes, used to
    /// detect "did the clipboard update after I copied". Only compared for
    /// equality, so any change-detecting value works (a content hash is fine).
    /// </summary>
    uint GetChangeToken();
}

internal sealed record ZetlClipboardImage(byte[] PngBytes, int Width, int Height);

/// <summary>
/// The capture-relevant clipboard formats observed at one change token.
/// </summary>
internal sealed record ZetlClipboardCaptureSnapshot(
    uint ChangeToken,
    string? Text,
    string? Html,
    IReadOnlyList<ZetlClipboardFormatData>? ReplayFormats,
    ZetlClipboardImage? Image,
    // The source app marked the content private; nothing of it was read.
    bool Private = false)
{
    public static ZetlClipboardCaptureSnapshot PrivateContent(uint changeToken) =>
        new(changeToken, null, null, null, null, Private: true);
}

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

internal enum ZetlClipboardWriteStatus
{
    Success,
    BackupIncomplete,
    StagingFailed,
    ClipboardUnavailable,
    EmptyFailed,
    WriteFailedRolledBack,
    WriteFailedRestoreFailed
}

internal sealed record ZetlClipboardWriteResult(
    ZetlClipboardWriteStatus Status,
    uint? FailedFormat = null,
    string? FailureReason = null)
{
    public bool Succeeded => Status == ZetlClipboardWriteStatus.Success;

    public bool ClipboardPreserved => Status is
        ZetlClipboardWriteStatus.Success
        or ZetlClipboardWriteStatus.BackupIncomplete
        or ZetlClipboardWriteStatus.StagingFailed
        or ZetlClipboardWriteStatus.ClipboardUnavailable
        or ZetlClipboardWriteStatus.EmptyFailed
        or ZetlClipboardWriteStatus.WriteFailedRolledBack;

    public static ZetlClipboardWriteResult Success { get; } = new(
        ZetlClipboardWriteStatus.Success);

    public static ZetlClipboardWriteResult FromLegacy(bool succeeded) =>
        succeeded
            ? Success
            : new(
                ZetlClipboardWriteStatus.StagingFailed,
                FailureReason: "the clipboard backend did not complete the write");
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
