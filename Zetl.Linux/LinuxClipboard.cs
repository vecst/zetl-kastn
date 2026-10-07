using System.Text;

namespace ZETL;

/// <summary>
/// Zetl's clipboard on Linux, over the desktop selection (MIME-typed, owned by
/// whichever app copied last). Mirrors the Windows backend's behavior:
/// captures come from one selection generation, private (password manager)
/// content is never read for capture, backups are exact copies of every type,
/// and staged pastes report when an app actually reads them.
/// </summary>
internal sealed class LinuxClipboard(
    ILinuxSelection selection,
    Func<byte[], string, ZetlClipboardImage?> normalizeImage,
    Action<string> log) : IClipboard, IDisposable
{
    /// <summary>KDE's (and KeePassXC's, and Klipper's) "do not keep this" marker.</summary>
    internal const string PasswordHint = "x-kde-passwordManagerHint";
    internal const string HtmlType = "text/html";
    internal const string PngType = "image/png";
    internal const string CalcEmbedType = "application/x-openoffice-embed-source-xml";
    internal const string CalcDescriptorType = "application/x-openoffice-objectdescriptor-xml";
    private const int MaxRichHtmlBytes = 25 * 1024 * 1024;
    private const long MaxBackupBytes = 256L * 1024 * 1024;

    // Clipboard monitors (Klipper, KDE Connect) read each new selection within a
    // few milliseconds, and not all of them honor the password hint. A staged
    // paste waits for them to go quiet before the paste is sent, so only reads
    // after that count as the paste landing.
    private static readonly TimeSpan MonitorQuiet = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan MonitorSettleLimit = TimeSpan.FromMilliseconds(200);

    /// <summary>Text types in order of preference; Zetl writes all of them.</summary>
    internal static readonly string[] TextTypes =
        ["text/plain;charset=utf-8", "UTF8_STRING", "text/plain", "STRING", "TEXT"];

    // X11 selection plumbing that Xwayland lists as types; none carries content.
    private static readonly HashSet<string> SelectionMetaTypes = new(StringComparer.Ordinal)
    {
        "TARGETS", "MULTIPLE", "TIMESTAMP", "SAVE_TARGETS", "DELETE", "INSERT_PROPERTY", "INSERT_SELECTION"
    };

    private volatile ZetlClipboardCaptureSnapshot? lastCapture;

    public uint GetChangeToken() => selection.Generation;

    public bool IsMarkedPrivate() => selection.GetMimeTypes(out _)?.Contains(PasswordHint) == true;

    // The single-format getters read regardless of the private marker, as on
    // Windows; only capture (Zetl taking a copy into its notes) honors it.
    public string? TryGetText() => Read(honorPrivate: false)?.Text;

    public string? TryGetHtml() => Read(honorPrivate: false)?.Html;

    public IReadOnlyList<ZetlClipboardFormatData>? TryGetReplayFormats() => Read(honorPrivate: false)?.ReplayFormats;

    public ZetlClipboardImage? TryGetImage() => Read(honorPrivate: false)?.Image;

    public ZetlClipboardCaptureSnapshot? TryCaptureContent()
    {
        // A burst of copies starts several observers that all want the same
        // generation; snapshots are immutable, so they share one.
        if (lastCapture is { } last && last.ChangeToken == selection.Generation) return last;
        var snapshot = Read(honorPrivate: true);
        if (snapshot is not null) lastCapture = snapshot;
        return snapshot;
    }

    public bool SetText(string text) => ReplaceText(text).Succeeded;

    public ZetlClipboardWriteResult ReplaceText(string text) => Write(TextData(text), "Clipboard text write");

    public bool SetRichText(string plainText, string html) => ReplaceRichText(plainText, html).Succeeded;

    public ZetlClipboardWriteResult ReplaceRichText(string plainText, string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return ReplaceText(plainText);
        var data = TextData(plainText);
        data[HtmlType] = HtmlBytes(html);
        return Write(data, "Rich clipboard write");
    }

    public bool SetImage(ZetlClipboardImage image) => ReplaceImage(image).Succeeded;

    public ZetlClipboardWriteResult ReplaceImage(ZetlClipboardImage image) =>
        Write(new Dictionary<string, byte[]> { [PngType] = image.PngBytes }, "Clipboard image write");

    public ZetlClipboardBackup CaptureBackup()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (selection.GetMimeTypes(out var generation) is not { } types)
            {
                return ZetlClipboardBackup.Incomplete("the clipboard is unavailable");
            }

            var content = types.Where(type => !SelectionMetaTypes.Contains(type)).Distinct().ToList();
            if (content.Count == 0) return ZetlClipboardBackup.FromRaw([]);
            if (selection.Read(content, out var readGeneration) is not { } data)
            {
                return ZetlClipboardBackup.Incomplete("the clipboard is unavailable");
            }

            if (readGeneration != generation) continue;
            var missing = content.Count(type => !data.ContainsKey(type));
            if (missing > 0)
            {
                return ZetlClipboardBackup.Incomplete(
                    $"the app that copied did not hand over {missing} of {content.Count} formats");
            }

            if (data.Values.Sum(bytes => (long)bytes.Length) > MaxBackupBytes)
            {
                return ZetlClipboardBackup.Incomplete("the clipboard content is too large to back up");
            }

            return ZetlClipboardBackup.FromRaw(
                content.Select(type => new ZetlClipboardFormatData(0, data[type], type)).ToList());
        }

        return ZetlClipboardBackup.Incomplete("the clipboard kept changing");
    }

    public bool RestoreBackup(ZetlClipboardBackup backup) => ReplaceWithBackup(backup).Succeeded;

    public ZetlClipboardWriteResult ReplaceWithBackup(ZetlClipboardBackup backup)
    {
        if (!backup.IsComplete)
        {
            return new(ZetlClipboardWriteStatus.BackupIncomplete, FailureReason: backup.FailureReason);
        }

        if (backup.RawFormats is { } raw)
        {
            if (raw.Count == 0)
            {
                lastCapture = null;
                return selection.Clear() is null
                    ? new(ZetlClipboardWriteStatus.EmptyFailed, FailureReason: "the clipboard could not be emptied")
                    : ZetlClipboardWriteResult.Success;
            }

            var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var format in raw)
            {
                if (format.RegisteredName is { Length: > 0 } type) data[type] = format.Data;
            }

            return Write(data, "Clipboard restore");
        }

        var portable = backup.Text is null ? new Dictionary<string, byte[]>() : TextData(backup.Text);
        if (!string.IsNullOrWhiteSpace(backup.Html)) portable[HtmlType] = HtmlBytes(backup.Html);
        if (backup.Image is not null) portable[PngType] = backup.Image.PngBytes;
        return Write(portable, "Clipboard restore");
    }

    public ZetlStagedPaste? StagePaste(
        string text,
        string? html,
        IReadOnlyList<ZetlClipboardFormatData>? replayFormats,
        ZetlClipboardImage? image)
    {
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (replayFormats is { Count: > 0 })
        {
            // Bundles captured on Linux name their MIME types; Windows bundles
            // name Windows formats, so those replay through text and HTML instead.
            foreach (var format in replayFormats)
            {
                if (format.RegisteredName is { } type && type.Contains('/')) data[type] = format.Data;
            }
        }

        if (data.Count == 0 && image is not null)
        {
            data[PngType] = image.PngBytes;
        }
        else if (!data.Keys.Any(TextTypes.Contains))
        {
            foreach (var (type, bytes) in TextData(text)) data[type] = bytes;
            if (!string.IsNullOrWhiteSpace(html) && !data.ContainsKey(HtmlType)) data[HtmlType] = HtmlBytes(html);
        }

        // Keeps the item out of clipboard history and monitors, Zetl's own copy
        // observers included, so the app being pasted into is the one that reads it.
        data[PasswordHint] = "secret"u8.ToArray();
        var staged = new ZetlStagedPaste();
        var armed = false;
        var lastMonitorRead = System.Diagnostics.Stopwatch.GetTimestamp();
        var generation = selection.Offer(
            data,
            sent: type =>
            {
                if (Volatile.Read(ref armed))
                {
                    if (type != PasswordHint) staged.MarkRead();
                }
                else
                {
                    Interlocked.Exchange(ref lastMonitorRead, System.Diagnostics.Stopwatch.GetTimestamp());
                }
            },
            replaced: staged.MarkReplaced);
        if (generation is null)
        {
            log("Staged paste skipped: the clipboard is unavailable.");
            return null;
        }

        var offeredAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref lastMonitorRead, Math.Max(Interlocked.Read(ref lastMonitorRead), offeredAt));
        while (System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref lastMonitorRead)) < MonitorQuiet
               && System.Diagnostics.Stopwatch.GetElapsedTime(offeredAt) < MonitorSettleLimit)
        {
            Thread.Sleep(5);
        }

        Volatile.Write(ref armed, true);
        lastCapture = null;
        staged.ChangeToken = generation.Value;
        return staged;
    }

    public void Dispose() => (selection as IDisposable)?.Dispose();

    private ZetlClipboardWriteResult Write(Dictionary<string, byte[]> data, string what)
    {
        lastCapture = null;
        if (selection.Offer(data) is not null) return ZetlClipboardWriteResult.Success;
        log($"{what} failed: the clipboard is unavailable.");
        return new(ZetlClipboardWriteStatus.ClipboardUnavailable, FailureReason: "the clipboard is unavailable");
    }

    /// <summary>
    /// Reads every format Zetl uses from one selection generation, retrying when
    /// the selection changes between listing its types and reading them.
    /// </summary>
    private ZetlClipboardCaptureSnapshot? Read(bool honorPrivate)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (selection.GetMimeTypes(out var generation) is not { } types) return null;
            if (honorPrivate && types.Contains(PasswordHint))
            {
                // Checked before any content is read, so a password never
                // enters Zetl's memory.
                return ZetlClipboardCaptureSnapshot.PrivateContent(generation);
            }

            var textType = TextTypes.FirstOrDefault(types.Contains);
            var htmlType = types.Contains(HtmlType) ? HtmlType : null;
            var imageType = types.Contains(PngType) ? PngType : types.FirstOrDefault(IsImageType);
            var replayTypes = types.Where(IsCalcNativeType).ToList();
            var wanted = new List<string>();
            if (textType is not null) wanted.Add(textType);
            if (htmlType is not null) wanted.Add(htmlType);
            if (imageType is not null) wanted.Add(imageType);
            wanted.AddRange(replayTypes);
            if (wanted.Count == 0) return new ZetlClipboardCaptureSnapshot(generation, null, null, null, null);

            if (selection.Read(wanted, out var readGeneration) is not { } data) return null;
            if (readGeneration != generation) continue;

            var text = textType is not null && data.TryGetValue(textType, out var textBytes)
                ? DecodeText(textBytes, textType)
                : null;
            var html = htmlType is not null
                && data.TryGetValue(htmlType, out var htmlBytes)
                && htmlBytes.Length <= MaxRichHtmlBytes
                    ? ExtractHtmlFragment(DecodeHtml(htmlBytes))
                    : null;
            var image = imageType is not null && data.TryGetValue(imageType, out var imageBytes)
                ? normalizeImage(imageBytes, imageType)
                : null;
            return new ZetlClipboardCaptureSnapshot(
                generation, text, html, ReplayBundle(data, textType, replayTypes), image);
        }

        return null;
    }

    /// <summary>
    /// LibreOffice Calc's self-contained native source, kept so a replayed
    /// paste lands as cells rather than text. Like Windows, a bundle exists
    /// only when Calc supplied its embed source alongside text.
    /// </summary>
    private static IReadOnlyList<ZetlClipboardFormatData>? ReplayBundle(
        IReadOnlyDictionary<string, byte[]> data,
        string? textType,
        IReadOnlyList<string> replayTypes)
    {
        if (textType is null
            || !data.ContainsKey(textType)
            || !replayTypes.Any(type => type.StartsWith(CalcEmbedType, StringComparison.Ordinal) && data.ContainsKey(type)))
        {
            return null;
        }

        var bundle = new List<ZetlClipboardFormatData>();
        foreach (var type in new[] { textType, HtmlType }.Concat(replayTypes))
        {
            if (data.TryGetValue(type, out var bytes)) bundle.Add(new ZetlClipboardFormatData(0, bytes, type));
        }

        return bundle.Sum(format => (long)format.Data.Length) <= MaxRichHtmlBytes ? bundle : null;
    }

    private static bool IsImageType(string type) => type.StartsWith("image/", StringComparison.Ordinal);

    private static bool IsCalcNativeType(string type) =>
        type.StartsWith(CalcEmbedType, StringComparison.Ordinal)
        || type.StartsWith(CalcDescriptorType, StringComparison.Ordinal);

    private static Dictionary<string, byte[]> TextData(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return TextTypes.ToDictionary(type => type, _ => bytes, StringComparer.Ordinal);
    }

    // Browsers mark the charset the same way, so receivers decode UTF-8.
    private static byte[] HtmlBytes(string fragment) =>
        Encoding.UTF8.GetBytes("<meta http-equiv=\"content-type\" content=\"text/html; charset=utf-8\">" + fragment);

    internal static string DecodeText(byte[] bytes, string type)
    {
        var text = type == "STRING" ? Encoding.Latin1.GetString(bytes) : Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        return text.TrimEnd('\0');
    }

    internal static string DecodeHtml(byte[] bytes)
    {
        // Some producers (older Mozilla builds among them) send UTF-16 with a byte-order mark.
        if (bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes).TrimStart('﻿').TrimEnd(' ');
    }

    /// <summary>
    /// The copied fragment, matching what Windows' CF_HTML yields: the part
    /// between the fragment markers when present, otherwise the body, without
    /// the charset meta tag browsers prepend.
    /// </summary>
    internal static string ExtractHtmlFragment(string html)
    {
        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";
        var start = html.IndexOf(startMarker, StringComparison.Ordinal);
        if (start >= 0)
        {
            start += startMarker.Length;
            var end = html.IndexOf(endMarker, start, StringComparison.Ordinal);
            if (end >= 0) return html[start..end];
        }

        var bodyOpen = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyOpen >= 0)
        {
            var contentStart = html.IndexOf('>', bodyOpen);
            var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (contentStart >= 0 && bodyClose > contentStart) return html[(contentStart + 1)..bodyClose];
        }

        var fragment = html.TrimStart();
        while (fragment.StartsWith("<meta", StringComparison.OrdinalIgnoreCase)
               && fragment.IndexOf('>') is var close and >= 0)
        {
            fragment = fragment[(close + 1)..].TrimStart();
        }

        return fragment;
    }
}
