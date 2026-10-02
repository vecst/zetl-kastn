using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Chordl;

namespace ZETL;

internal sealed class AvaloniaWindowsClipboard : IClipboard, IDisposable
{
    private const uint Bitmap = 2;
    private const uint MetafilePicture = 3;
    private const uint UnicodeText = 13;
    private const uint Dib = 8;
    private const uint Palette = 9;
    private const uint EnhancedMetafile = 14;
    private const uint DibV5 = 17;
    private const uint OwnerDisplay = 0x0080;
    private const uint DisplayText = 0x0081;
    private const uint DisplayBitmap = 0x0082;
    private const uint DisplayMetafilePicture = 0x0083;
    private const uint DisplayEnhancedMetafile = 0x008E;
    private const uint PrivateFormatFirst = 0x0200;
    private const uint PrivateFormatLast = 0x02FF;
    private const uint GdiObjectFormatFirst = 0x0300;
    private const uint GdiObjectFormatLast = 0x03FF;
    private const uint Moveable = 0x0002;
    private const int ClipboardAttempts = 5;
    private const int HwndMessage = -3;
    private const int MaxBackupFormats = 128;
    private const ulong MaxBackupBytes = 256UL * 1024 * 1024;
    private const int MaxStoredRichHtmlBytes = 25 * 1024 * 1024;

    private readonly Action<string> log;
    private readonly Func<IntPtr> createOwnerWindow;
    private IntPtr ownerWindow;
    private static readonly uint Png = RegisterClipboardFormat("PNG");
    private static readonly uint Html = RegisterClipboardFormat("HTML Format");
    private static readonly uint ExcludeFromMonitoring =
        RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint CanIncludeInHistory =
        RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint ClipboardViewerIgnore =
        RegisterClipboardFormat("Clipboard Viewer Ignore");
    private static readonly uint EnterpriseDataProtection =
        RegisterClipboardFormat("EnterpriseDataProtectionId");
    private static readonly HashSet<string> NativeReplayFormatNames = new(
        StringComparer.Ordinal)
    {
        "Star Embed Source (XML)",
        "Star Object Descriptor (XML)"
    };

    internal static uint UnicodeTextFormat => UnicodeText;
    internal static uint HtmlClipboardFormat => Html;
    internal static uint PngClipboardFormat => Png;
    internal static uint DibClipboardFormat => Dib;

    // ownerWindowFactory is a test seam: pass `() => IntPtr.Zero` to simulate a
    // failed owner-window creation and verify writes refuse without wiping the
    // clipboard. Production uses the real message-only window.
    public AvaloniaWindowsClipboard(Action<string> log, Func<IntPtr>? ownerWindowFactory = null)
    {
        this.log = log;
        createOwnerWindow = ownerWindowFactory ?? (() => CreateOwnerWindow(log));
        ownerWindow = createOwnerWindow();
    }

    public string? TryGetText()
    {
        if (!TryOpen())
        {
            return null;
        }

        try
        {
            return ReadTextFromOpenClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    // Reads no further than the block Windows hands over. Text from another app
    // need not end in a terminator inside its block, and scanning for one ran
    // past the end into memory that isn't Zetl's: an access violation, or with
    // a busy heap, the heap corruption seen during fast repeated copies.
    private static string? ReadTextFromOpenClipboard()
    {
        if (ReadClipboardBytes(UnicodeText) is not { } bytes)
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    public string? TryGetHtml()
    {
        if (!TryOpen())
        {
            return null;
        }

        try
        {
            return ReadHtmlFromOpenClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static string? ReadHtmlFromOpenClipboard()
    {
        var bytes = ReadClipboardBytes(Html);
        return bytes is { Length: <= MaxStoredRichHtmlBytes }
            && TryExtractHtmlFragment(bytes, out var fragment)
                ? fragment
                : null;
    }

    public IReadOnlyList<ZetlClipboardFormatData>? TryGetReplayFormats()
    {
        if (!TryOpen())
        {
            return null;
        }

        try
        {
            return ReadReplayFormatsFromOpenClipboard();
        }
        finally
        {
            CloseClipboard();
        }

    }

    private static IReadOnlyList<ZetlClipboardFormatData>? ReadReplayFormatsFromOpenClipboard()
    {
        var formats = new List<ZetlClipboardFormatData>();
        var hasNativeEmbed = false;
        var hasUnicodeText = false;
        var totalBytes = 0;
        uint format = 0;
        while ((format = EnumClipboardFormats(format)) != 0)
        {
            var registeredName = GetRegisteredFormatName(format);
            var isNative = registeredName is not null
                && NativeReplayFormatNames.Contains(registeredName);
            if (format != UnicodeText && format != Html && !isNative)
            {
                continue;
            }

            var data = ReadClipboardBytes(format);
            var required = format == UnicodeText
                || string.Equals(
                    registeredName,
                    "Star Embed Source (XML)",
                    StringComparison.Ordinal);
            if (data is null)
            {
                if (required)
                {
                    return null;
                }
                continue;
            }

            if (data.Length > MaxStoredRichHtmlBytes - totalBytes)
            {
                return null;
            }

            formats.Add(new ZetlClipboardFormatData(
                format,
                data,
                registeredName));
            totalBytes += data.Length;
            hasUnicodeText |= format == UnicodeText;
            hasNativeEmbed |= string.Equals(
                registeredName,
                "Star Embed Source (XML)",
                StringComparison.Ordinal);
        }

        // HTML alone already has the lightweight RichHtml path. Persist a raw
        // bundle only when Calc supplied its self-contained native source.
        return hasNativeEmbed && hasUnicodeText ? formats : null;
    }

    internal static bool TryExtractHtmlFragment(
        byte[] clipboardBytes,
        out string? fragment)
    {
        fragment = null;
        if (clipboardBytes.Length == 0)
        {
            return false;
        }

        // CF_HTML offsets are byte offsets into the UTF-8 payload, not string
        // indexes. Read only the ASCII header to locate them, then decode the
        // exact fragment slice so non-ASCII formatting content stays intact.
        var headerLength = Math.Min(clipboardBytes.Length, 4096);
        var header = Encoding.ASCII.GetString(clipboardBytes, 0, headerLength);
        if (TryReadHtmlOffset(header, "StartFragment:", out var start)
            && TryReadHtmlOffset(header, "EndFragment:", out var end)
            && start >= 0
            && end >= start
            && end <= clipboardBytes.Length)
        {
            fragment = Encoding.UTF8.GetString(clipboardBytes, start, end - start);
            return true;
        }

        // A few producers emit missing or unusable offsets but include the
        // standard fragment markers. Keep this fallback byte-safe by finding
        // the ASCII marker bytes before decoding the UTF-8 slice.
        ReadOnlySpan<byte> bytes = clipboardBytes;
        ReadOnlySpan<byte> startMarker = "<!--StartFragment-->"u8;
        ReadOnlySpan<byte> endMarker = "<!--EndFragment-->"u8;
        var markerStart = bytes.IndexOf(startMarker);
        if (markerStart < 0)
        {
            return false;
        }

        markerStart += startMarker.Length;
        var markerEnd = bytes[markerStart..].IndexOf(endMarker);
        if (markerEnd < 0)
        {
            return false;
        }

        fragment = Encoding.UTF8.GetString(bytes.Slice(markerStart, markerEnd));
        return true;
    }

    private static bool TryReadHtmlOffset(
        string header,
        string label,
        out int offset)
    {
        offset = 0;
        var labelIndex = header.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (labelIndex < 0)
        {
            return false;
        }

        var index = labelIndex + label.Length;
        while (index < header.Length && char.IsWhiteSpace(header[index]))
        {
            index++;
        }

        var digitStart = index;
        while (index < header.Length && char.IsAsciiDigit(header[index]))
        {
            var digit = header[index] - '0';
            if (offset > (int.MaxValue - digit) / 10)
            {
                return false;
            }

            offset = offset * 10 + digit;
            index++;
        }

        return index > digitStart;
    }

    public ZetlClipboardImage? TryGetImage()
    {
        if (!TryOpen())
        {
            return null;
        }

        try
        {
            return ReadImageFromOpenClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static byte[]? ReadClipboardBytes(uint format)
    {
        if (format == 0)
        {
            return null;
        }

        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var size = GlobalSize(handle).ToUInt64();
        if (size == 0 || size > 256UL * 1024 * 1024 || size > int.MaxValue)
        {
            return null;
        }

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    internal static byte[]? AddBitmapFileHeader(byte[] dib)
    {
        if (dib.Length < 40)
        {
            return null;
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(0, 4));
        var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14, 2));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16, 4));
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32, 4));
        if (headerSize < 40 || headerSize > dib.Length)
        {
            return null;
        }

        var paletteEntries = colorsUsed != 0
            ? colorsUsed
            : bitsPerPixel <= 8 ? 1u << bitsPerPixel : 0u;
        var masks = compression == 3 && headerSize == 40 ? 12 : 0;
        int pixelOffset;
        try
        {
            pixelOffset = checked(14 + headerSize + masks + (int)paletteEntries * 4);
        }
        catch (OverflowException)
        {
            return null;
        }
        if (pixelOffset > dib.Length + 14)
        {
            return null;
        }

        var bitmap = new byte[dib.Length + 14];
        bitmap[0] = (byte)'B';
        bitmap[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(2, 4), bitmap.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(10, 4), pixelOffset);
        dib.CopyTo(bitmap, 14);
        return bitmap;
    }

    internal static bool TryNormalizeImage(byte[] source, out ZetlClipboardImage? image)
    {
        try
        {
            using var input = new MemoryStream(source, writable: false);
            using var bitmap = new Bitmap(input);
            using var output = new MemoryStream();
            bitmap.Save(output);
            image = new ZetlClipboardImage(
                output.ToArray(),
                bitmap.PixelSize.Width,
                bitmap.PixelSize.Height);
            return image.PngBytes.Length > 0 && image.Width > 0 && image.Height > 0;
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
        {
            image = null;
            return false;
        }
    }

    public ZetlClipboardBackup CaptureBackup()
    {
        if (!TryOpen())
        {
            return ZetlClipboardBackup.Incomplete("the clipboard is temporarily unavailable");
        }

        var formats = new List<ZetlClipboardFormatData>();
        var nonMemoryFormats = new List<uint>();
        var enterpriseProtected = false;
        try
        {
            ulong totalBytes = 0;
            uint format = 0;
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                format = EnumClipboardFormats(format);
                if (format == 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 0)
                    {
                        return ZetlClipboardBackup.Incomplete(
                            $"clipboard format enumeration failed (error {error})");
                    }
                    break;
                }

                // Windows marks copies with this format even on unmanaged
                // machines (some apps add it to every copy), usually with no
                // data at all. Only a marker naming an enterprise makes the
                // copy protected; the marker itself is never backed up.
                if (format == EnterpriseDataProtection)
                {
                    enterpriseProtected |= ReadClipboardBytes(format) is { } id
                        && Encoding.Unicode.GetString(id, 0, id.Length & ~1).Trim('\0', ' ').Length > 0;
                    continue;
                }

                if (format == EnhancedMetafile)
                {
                    var metafileHandle = GetClipboardData(format);
                    var data = metafileHandle == IntPtr.Zero
                        ? null
                        : ReadEnhancedMetafileBytes(metafileHandle);
                    if (data is null
                        || (ulong)data.Length > MaxBackupBytes - totalBytes)
                    {
                        return ZetlClipboardBackup.Incomplete(
                            "CF_ENHMETAFILE could not be serialized safely or the clipboard is too large");
                    }

                    formats.Add(new ZetlClipboardFormatData(format, data));
                    totalBytes += (ulong)data.Length;
                    continue;
                }

                if (formats.Count >= MaxBackupFormats)
                {
                    return ZetlClipboardBackup.Incomplete(
                        $"the clipboard contains more than {MaxBackupFormats} formats");
                }

                if (UsesNonMemoryHandle(format))
                {
                    // Some advertised handle formats are synthesized by Windows
                    // from a canonical format. Defer the decision until all
                    // formats are known so the canonical payload can cover them.
                    nonMemoryFormats.Add(format);
                    continue;
                }

                var handle = GetClipboardData(format);
                if (handle == IntPtr.Zero)
                {
                    return ZetlClipboardBackup.Incomplete(
                        $"{DescribeFormat(format)} could not be read");
                }

                var size = GlobalSize(handle).ToUInt64();
                if (size == 0
                    || size > int.MaxValue
                    || size > MaxBackupBytes - totalBytes)
                {
                    return ZetlClipboardBackup.Incomplete(
                        $"{DescribeFormat(format)} is not a restorable memory payload or the clipboard is too large");
                }

                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    return ZetlClipboardBackup.Incomplete(
                        $"{DescribeFormat(format)} could not be locked for backup");
                }

                try
                {
                    var data = new byte[(int)size];
                    Marshal.Copy(pointer, data, 0, data.Length);
                    formats.Add(new ZetlClipboardFormatData(format, data));
                    totalBytes += size;
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
        }
        finally
        {
            CloseClipboard();
        }

        // Protected enterprise content isn't copied into a backup; writes still
        // go ahead without one.
        if (enterpriseProtected)
        {
            return ZetlClipboardBackup.Incomplete(
                "the clipboard holds Windows-protected enterprise data");
        }

        var unsupportedFormat = nonMemoryFormats.FirstOrDefault(format =>
            !IsSynthesizedFormatCovered(format, formats));
        if (unsupportedFormat != 0)
        {
            return ZetlClipboardBackup.Incomplete(
                $"{DescribeFormat(unsupportedFormat)} cannot be restored safely");
        }

        return ZetlClipboardBackup.FromRaw(formats);
    }

    public bool RestoreBackup(ZetlClipboardBackup backup)
    {
        return ReplaceWithBackup(backup).Succeeded;
    }

    public ZetlClipboardWriteResult ReplaceWithBackup(ZetlClipboardBackup backup)
    {
        if (!backup.IsComplete || backup.RawFormats is null)
        {
            log("Clipboard restore refused: the backup is incomplete or belongs to another backend.");
            return new(
                ZetlClipboardWriteStatus.BackupIncomplete,
                FailureReason: "the requested backup is incomplete or belongs to another backend");
        }

        return ReplaceFormats(backup.RawFormats, "Clipboard restore");
    }

    private ZetlClipboardWriteResult ReplaceFormats(
        IReadOnlyList<ZetlClipboardFormatData> targetFormats,
        string operation)
    {
        if (!EnsureOwnerWindow())
        {
            log($"{operation} skipped: no owner window available; clipboard left intact.");
            return new(
                ZetlClipboardWriteStatus.ClipboardUnavailable,
                FailureReason: "no clipboard owner window is available");
        }

        // Back up the current clipboard so a failed write can be rolled back. When
        // it holds something that cannot be backed up (a browser's virtual-file
        // image, delay-rendered data), write anyway: every caller is replacing the
        // clipboard on purpose, and refusing would block every later write until
        // the user happened to copy something else.
        var original = CaptureBackup();
        var originalFormats = original.IsComplete ? original.RawFormats : null;
        if (originalFormats is null)
        {
            log($"{operation}: {original.FailureReason ?? "the current clipboard could not be backed up completely"}; writing without a rollback.");
        }

        var target = new List<ZetlStagedClipboardFormat<NativeClipboardPayload>>();
        var rollback = originalFormats is null
            ? null
            : new List<ZetlStagedClipboardFormat<NativeClipboardPayload>>();
        var opened = false;
        try
        {
            if (!TryStageFormats(targetFormats, target, out var failedFormat, out var failureReason)
                || (rollback is not null
                    && !TryStageFormats(originalFormats!, rollback, out failedFormat, out failureReason)))
            {
                log($"{operation} staging failed: {failureReason}; clipboard left intact.");
                return new(
                    ZetlClipboardWriteStatus.StagingFailed,
                    failedFormat,
                    failureReason);
            }

            if (!TryOpen())
            {
                return new(
                    ZetlClipboardWriteStatus.ClipboardUnavailable,
                    FailureReason: "the clipboard is temporarily unavailable");
            }
            opened = true;

            var result = ZetlNativeClipboardTransaction.Execute(
                target,
                rollback,
                EmptyClipboard,
                (format, payload) =>
                    SetClipboardData(format, payload.Handle) != IntPtr.Zero);
            if (result.Status == ZetlClipboardWriteStatus.WriteFailedRolledBack)
            {
                log(
                    $"{operation} failed while writing {DescribeFormat(result.FailedFormat ?? 0)}; the original clipboard was restored.");
            }
            else if (result.Status == ZetlClipboardWriteStatus.WriteFailedRestoreFailed)
            {
                log(
                    $"{operation} failed and the original clipboard could not be restored completely ({result.FailureReason}).");
            }
            else if (result.Status == ZetlClipboardWriteStatus.EmptyFailed)
            {
                log($"{operation} failed: EmptyClipboard error {Marshal.GetLastWin32Error()}.");
            }

            return result;
        }
        finally
        {
            ReleaseOwnedPayloads(target);
            if (rollback is not null)
            {
                ReleaseOwnedPayloads(rollback);
            }
            if (opened)
            {
                CloseClipboard();
            }
        }
    }

    private static ZetlClipboardImage? ReadImageFromOpenClipboard()
    {
        var pngBytes = ReadClipboardBytes(Png);
        if (pngBytes is not null && TryCreatePngSnapshot(pngBytes, out var pngImage))
        {
            return pngImage;
        }

        foreach (var format in new[] { DibV5, Dib })
        {
            var dibBytes = ReadClipboardBytes(format);
            if (dibBytes is null)
            {
                continue;
            }

            var bitmapBytes = AddBitmapFileHeader(dibBytes);
            if (bitmapBytes is not null && TryNormalizeImage(bitmapBytes, out var image))
            {
                return image;
            }
        }

        return null;
    }

    internal static bool TryCreatePngSnapshot(
        byte[] pngBytes,
        out ZetlClipboardImage? image)
    {
        ReadOnlySpan<byte> png = pngBytes;
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        ReadOnlySpan<byte> ihdr = "IHDR"u8;
        ReadOnlySpan<byte> iend = "IEND"u8;
        if (png.Length < 33
            || !png[..8].SequenceEqual(signature)
            || BinaryPrimitives.ReadUInt32BigEndian(png.Slice(8, 4)) != 13
            || !png.Slice(12, 4).SequenceEqual(ihdr))
        {
            image = null;
            return false;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            image = null;
            return false;
        }

        var offset = 8;
        var foundEnd = false;
        while (offset <= png.Length - 12)
        {
            var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(
                png.Slice(offset, 4));
            var chunkEnd = (ulong)offset + 12UL + chunkLength;
            if (chunkEnd > (ulong)png.Length)
            {
                image = null;
                return false;
            }

            var chunkType = png.Slice(offset + 4, 4);
            if (chunkType.SequenceEqual(iend))
            {
                foundEnd = chunkLength == 0 && chunkEnd == (ulong)png.Length;
                break;
            }

            offset = (int)chunkEnd;
        }

        if (!foundEnd)
        {
            image = null;
            return false;
        }

        image = new ZetlClipboardImage(pngBytes, (int)width, (int)height);
        return true;
    }

    // The last capture, handed out again while the clipboard hasn't changed:
    // a burst of copies starts several observers, and they all want the same
    // generation. Snapshots are immutable, so sharing one is safe.
    private volatile ZetlClipboardCaptureSnapshot? lastCapture;

    public ZetlClipboardCaptureSnapshot? TryCaptureContent()
    {
        if (lastCapture is { } last && last.ChangeToken == GetClipboardSequenceNumber())
        {
            return last;
        }

        if (!TryOpen())
        {
            return null;
        }

        try
        {
            // Holding OpenClipboard prevents another process from replacing the
            // clipboard between these reads. The recorded sequence therefore
            // identifies the generation shared by every returned format.
            var changeToken = GetClipboardSequenceNumber();
            if (IsPrivateOnOpenClipboard())
            {
                // Checked before any content format is read, so a password is
                // never pulled into Zetl's memory at all.
                return ZetlClipboardCaptureSnapshot.PrivateContent(changeToken);
            }

            ZetlTrace.Write("clipboard capture: text");
            var text = ReadTextFromOpenClipboard();
            ZetlTrace.Write("clipboard capture: html");
            var html = ReadHtmlFromOpenClipboard();
            ZetlTrace.Write("clipboard capture: replay formats");
            var replayFormats = ReadReplayFormatsFromOpenClipboard();
            ZetlTrace.Write("clipboard capture: image");
            var image = ReadImageFromOpenClipboard();
            ZetlTrace.Write("clipboard capture: done");
            var snapshot = new ZetlClipboardCaptureSnapshot(
                changeToken,
                text,
                html,
                replayFormats,
                image);
            lastCapture = snapshot;
            return snapshot;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private bool TryStageFormats(
        IReadOnlyList<ZetlClipboardFormatData> formats,
        List<ZetlStagedClipboardFormat<NativeClipboardPayload>> staged,
        out uint? failedFormat,
        out string failureReason)
    {
        failedFormat = null;
        failureReason = "";
        var seenFormats = new HashSet<uint>();
        foreach (var item in formats)
        {
            var format = ResolveStoredFormat(item);
            if (format == 0
                || !seenFormats.Add(format)
                || item.Data.Length == 0
                || UsesNonMemoryHandle(format))
            {
                failedFormat = format == 0 ? item.Format : format;
                failureReason = "a stored clipboard format payload is invalid or cannot be transferred";
                return false;
            }

            var isEnhancedMetafile = format == EnhancedMetafile;
            var handle = isEnhancedMetafile
                ? SetEnhMetaFileBits((uint)item.Data.Length, item.Data)
                : AllocateGlobal(item.Data);
            if (handle == IntPtr.Zero)
            {
                failedFormat = format;
                failureReason = $"could not allocate {DescribeFormat(format)}";
                return false;
            }

            staged.Add(new(
                format,
                new NativeClipboardPayload(handle, isEnhancedMetafile)));
        }

        return true;
    }

    private static void ReleaseOwnedPayloads(
        IEnumerable<ZetlStagedClipboardFormat<NativeClipboardPayload>> staged)
    {
        foreach (var item in staged.Where(item => !item.Transferred))
        {
            if (item.Payload.IsEnhancedMetafile)
            {
                DeleteEnhMetaFile(item.Payload.Handle);
            }
            else
            {
                GlobalFree(item.Payload.Handle);
            }
        }
    }

    private sealed record NativeClipboardPayload(
        IntPtr Handle,
        bool IsEnhancedMetafile);

    private static bool UsesNonMemoryHandle(uint format) =>
        format is Bitmap or MetafilePicture or Palette
            or OwnerDisplay or DisplayText or DisplayBitmap
            or DisplayMetafilePicture or DisplayEnhancedMetafile
        || format is >= PrivateFormatFirst and <= PrivateFormatLast
        || format is >= GdiObjectFormatFirst and <= GdiObjectFormatLast;

    private static uint ResolveStoredFormat(ZetlClipboardFormatData item)
    {
        if (item.RegisteredName is null)
        {
            return item.Format;
        }

        var name = item.RegisteredName.Trim();
        return name.Length is > 0 and <= 255
            ? RegisterClipboardFormat(name)
            : 0;
    }

    internal static bool IsSynthesizedFormatCovered(
        uint format,
        IReadOnlyList<ZetlClipboardFormatData> capturedFormats)
    {
        var hasFormat = (uint candidate) =>
            capturedFormats.Any(item => item.Format == candidate);
        return format switch
        {
            // Windows converts between the enhanced and legacy metafile formats.
            MetafilePicture => hasFormat(EnhancedMetafile),
            // Windows creates bitmap and palette handles from either DIB format.
            Bitmap or Palette => hasFormat(Dib) || hasFormat(DibV5),
            _ => false,
        };
    }

    private static string DescribeFormat(uint format)
    {
        var standardName = format switch
        {
            Bitmap => "CF_BITMAP",
            MetafilePicture => "CF_METAFILEPICT",
            Dib => "CF_DIB",
            Palette => "CF_PALETTE",
            UnicodeText => "CF_UNICODETEXT",
            EnhancedMetafile => "CF_ENHMETAFILE",
            DibV5 => "CF_DIBV5",
            OwnerDisplay => "CF_OWNERDISPLAY",
            DisplayText => "CF_DSPTEXT",
            DisplayBitmap => "CF_DSPBITMAP",
            DisplayMetafilePicture => "CF_DSPMETAFILEPICT",
            DisplayEnhancedMetafile => "CF_DSPENHMETAFILE",
            _ => null,
        };
        if (standardName is not null)
        {
            return standardName;
        }

        var registeredName = GetRegisteredFormatName(format);
        return registeredName is not null
            ? $"clipboard format {registeredName}"
            : $"clipboard format {format}";
    }

    public bool IsMarkedPrivate()
    {
        if (!TryOpen())
        {
            return false;
        }

        try
        {
            return IsPrivateOnOpenClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    // The conventions password managers and other apps use to keep a copy out
    // of clipboard monitors and history: Windows' own clipboard history honors
    // ExcludeClipboardContentFromMonitorProcessing and a zero
    // CanIncludeInClipboardHistory; older tools set Clipboard Viewer Ignore.
    private static bool IsPrivateOnOpenClipboard()
    {
        if (IsClipboardFormatAvailable(ExcludeFromMonitoring)
            || IsClipboardFormatAvailable(ClipboardViewerIgnore))
        {
            return true;
        }

        return IsClipboardFormatAvailable(CanIncludeInHistory)
            && ReadClipboardBytes(CanIncludeInHistory) is { Length: >= 4 } allowed
            && BitConverter.ToUInt32(allowed, 0) == 0;
    }

    private static string? GetRegisteredFormatName(uint format)
    {
        var name = new StringBuilder(256);
        return GetClipboardFormatName(format, name, name.Capacity) > 0
            ? name.ToString()
            : null;
    }

    private static byte[]? ReadEnhancedMetafileBytes(IntPtr handle)
    {
        var size = GetEnhMetaFileBits(handle, 0, IntPtr.Zero);
        if (size == 0 || size > int.MaxValue)
        {
            return null;
        }

        var data = new byte[(int)size];
        return GetEnhMetaFileBits(handle, size, data) == size
            ? data
            : null;
    }

    public bool SetText(string text)
    {
        return ReplaceText(text).Succeeded;
    }

    public ZetlClipboardWriteResult ReplaceText(string text)
    {
        return ReplaceFormats(
            [new ZetlClipboardFormatData(UnicodeText, Encoding.Unicode.GetBytes(text + '\0'))],
            "Clipboard text write");
    }

    public bool SetRichText(string plainText, string html)
    {
        return ReplaceRichText(plainText, html).Succeeded;
    }

    public ZetlClipboardWriteResult ReplaceRichText(string plainText, string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return ReplaceText(plainText);
        }

        return ReplaceFormats(
            [
                new ZetlClipboardFormatData(
                    UnicodeText,
                    Encoding.Unicode.GetBytes(plainText + '\0')),
                new ZetlClipboardFormatData(Html, BuildHtmlClipboardBytes(html))
            ],
            "Rich clipboard write");
    }

    public bool SetImage(ZetlClipboardImage image)
    {
        return ReplaceImage(image).Succeeded;
    }

    public ZetlClipboardWriteResult ReplaceImage(ZetlClipboardImage image)
    {
        byte[] dib;
        try
        {
            dib = CreateDib(image.PngBytes);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            log($"Clipboard image write failed: {ex.Message}");
            return new(
                ZetlClipboardWriteStatus.StagingFailed,
                FailureReason: ex.Message);
        }

        return ReplaceFormats(
            [
                new ZetlClipboardFormatData(Png, image.PngBytes),
                new ZetlClipboardFormatData(Dib, dib)
            ],
            "Clipboard image write");
    }

    private static byte[] BuildHtmlClipboardBytes(string fragment)
    {
        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";
        const string prefix = "<html><body>";
        const string suffix = "</body></html>";
        var html = prefix + startMarker + fragment + endMarker + suffix;
        var headerTemplate =
            "Version:0.9\r\n"
            + "StartHTML:{0:D10}\r\n"
            + "EndHTML:{1:D10}\r\n"
            + "StartFragment:{2:D10}\r\n"
            + "EndFragment:{3:D10}\r\n";
        var placeholder = string.Format(
            CultureInfo.InvariantCulture,
            headerTemplate,
            0,
            0,
            0,
            0);
        var startHtml = Encoding.UTF8.GetByteCount(placeholder);
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(prefix + startMarker);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = startHtml + Encoding.UTF8.GetByteCount(html);
        var header = string.Format(
            CultureInfo.InvariantCulture,
            headerTemplate,
            startHtml,
            endHtml,
            startFragment,
            endFragment);
        return Encoding.UTF8.GetBytes(header + html);
    }

    private static IntPtr AllocateGlobal(byte[] bytes)
    {
        var handle = GlobalAlloc(Moveable, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            return IntPtr.Zero;
        }

        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    internal static byte[] CreateDib(byte[] pngBytes)
    {
        using var source = SKBitmap.Decode(pngBytes)
            ?? throw new InvalidDataException("The PNG image could not be decoded.");
        var info = new SKImageInfo(
            source.Width,
            source.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        if (!source.CopyTo(bitmap, SKColorType.Bgra8888))
        {
            throw new InvalidDataException("The PNG image could not be converted for the Windows clipboard.");
        }

        var rowBytes = checked(source.Width * 4);
        var pixelBytes = checked(rowBytes * source.Height);
        var dib = new byte[40 + pixelBytes];
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4, 4), source.Width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8, 4), -source.Height);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14, 2), 32);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20, 4), pixelBytes);
        for (var row = 0; row < source.Height; row++)
        {
            Marshal.Copy(
                bitmap.GetPixels() + row * bitmap.RowBytes,
                dib,
                40 + row * rowBytes,
                rowBytes);
        }

        return dib;
    }

    public uint GetChangeToken()
    {
        return GetClipboardSequenceNumber();
    }

    // Empties the clipboard. Used by the self-test to restore a no-text starting
    // state; not part of IClipboard. Returns false if it could not be emptied.
    public bool Clear()
    {
        if (!EnsureOwnerWindow() || !TryOpen())
        {
            return false;
        }

        try
        {
            return EmptyClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    // Self-test only: advertise a delay-rendered format that is never supplied,
    // like a browser's virtual-file image (FileContents). Reading it fails, so
    // the clipboard cannot be backed up. Returns false if it could not be placed.
    public bool PlaceUnreadableFormatForSelfTest(string formatName)
    {
        if (!EnsureOwnerWindow() || !TryOpen())
        {
            return false;
        }

        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            // Delayed rendering returns NULL on success too; the error code decides.
            Marshal.SetLastPInvokeError(0);
            SetClipboardData(RegisterClipboardFormat(formatName), IntPtr.Zero);
            return Marshal.GetLastPInvokeError() == 0;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void Dispose()
    {
        if (ownerWindow != IntPtr.Zero)
        {
            DestroyWindow(ownerWindow);
            ownerWindow = IntPtr.Zero;
        }
    }

    // True once a clipboard owner window exists. Retries creation in case the
    // constructor's attempt failed transiently, so writes can recover rather than
    // being refused for the whole session.
    private bool EnsureOwnerWindow()
    {
        if (ownerWindow == IntPtr.Zero)
        {
            ownerWindow = createOwnerWindow();
        }

        return ownerWindow != IntPtr.Zero;
    }

    // Every open-to-close span holds this gate, so only one thread in Zetl has
    // the clipboard open at a time. Windows ties an open clipboard to the owner
    // window, not the thread: a second thread opening with the same window
    // succeeds too, and when the first closes, the next app to copy frees the
    // data the second is still reading. That use-after-free is what corrupted
    // the heap during fast repeated Ctrl+C on the desktop.
    private static readonly object OpenGate = new();

    // For --clipboard-stress: one open-to-close pass doing only the named part
    // of a read, so a crash can be pinned to the call that causes it.
    internal void StressPass(string part)
    {
        if (!EnsureOwnerWindow() || !TryOpen())
        {
            return;
        }

        try
        {
            switch (part)
            {
                case "open":
                    break;
                case "data":
                    GetClipboardData(UnicodeText);
                    break;
                case "lock":
                    var handle = GetClipboardData(UnicodeText);
                    if (handle != IntPtr.Zero && GlobalLock(handle) != IntPtr.Zero)
                    {
                        GlobalUnlock(handle);
                    }

                    break;
                case "text":
                    ReadTextFromOpenClipboard();
                    break;
                case "private":
                    IsPrivateOnOpenClipboard();
                    break;
                case "formats":
                    ReadReplayFormatsFromOpenClipboard();
                    break;
                case "html":
                    ReadHtmlFromOpenClipboard();
                    break;
                case "image":
                    ReadImageFromOpenClipboard();
                    break;
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    // Taken with the gate held; CloseClipboard releases both.
    private bool TryOpen()
    {
        Monitor.Enter(OpenGate);
        for (var attempt = 0; attempt < ClipboardAttempts; attempt++)
        {
            // Open with our own owner window rather than a NULL association, so
            // EmptyClipboard/SetClipboardData behave like a normal clipboard app.
            if (OpenClipboard(ownerWindow))
            {
                return true;
            }

            Thread.Sleep(5);
        }

        Monitor.Exit(OpenGate);
        log($"Windows clipboard was temporarily unavailable (error {Marshal.GetLastWin32Error()}).");
        return false;
    }

    // Closes a clipboard opened by TryOpen and lets the next thread in.
    private static void CloseClipboard()
    {
        try
        {
            NativeCloseClipboard();
        }
        finally
        {
            Monitor.Exit(OpenGate);
        }
    }

    // A message-only window to own the clipboard. "STATIC" is a system-registered
    // class, so no class registration is needed; HWND_MESSAGE makes it invisible
    // and pump-light. If creation fails we return Zero and SetText refuses to
    // write rather than opening the clipboard with a NULL owner.
    private static IntPtr CreateOwnerWindow(Action<string> log)
    {
        var window = CreateWindowEx(
            0,
            "STATIC",
            "ZetlClipboardOwner",
            0,
            0,
            0,
            0,
            0,
            new IntPtr(HwndMessage),
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            log($"Clipboard owner window unavailable (error {Marshal.GetLastWin32Error()}); writes will be refused until one can be created.");
        }

        return window;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", EntryPoint = "CloseClipboard")]
    private static extern bool NativeCloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClipboardFormatNameW")]
    private static extern int GetClipboardFormatName(
        uint format,
        StringBuilder formatName,
        int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr memory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern uint GetEnhMetaFileBits(
        IntPtr enhancedMetafile,
        uint bufferSize,
        IntPtr data);

    [DllImport("gdi32.dll", SetLastError = true, EntryPoint = "GetEnhMetaFileBits")]
    private static extern uint GetEnhMetaFileBits(
        IntPtr enhancedMetafile,
        uint bufferSize,
        [Out] byte[] data);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SetEnhMetaFileBits(uint bufferSize, byte[] data);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteEnhMetaFile(IntPtr enhancedMetafile);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
