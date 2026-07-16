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

internal static class ZetlPlatformServices
{
    public static IKeyboardBackend CreateKeyboard(
        Action<string> log,
        bool allowInjectedInputForTesting)
    {
        return OperatingSystem.IsWindows()
            ? new AvaloniaWindowsKeyboardBackend(log, allowInjectedInputForTesting)
            : new UnsupportedKeyboardBackend(log);
    }

    public static IClipboard CreateClipboard(Action<string> log)
    {
        return OperatingSystem.IsWindows()
            ? new AvaloniaWindowsClipboard(log)
            : new UnsupportedClipboard(log);
    }
}

internal sealed class AvaloniaWindowsKeyboardBackend(
    Action<string> log,
    bool allowInjectedInputForTesting) : IKeyboardBackend
{
    private const int WhKeyboardLl = 13;
    private const int LlkInjected = 0x10;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private readonly HashSet<int> downKeys = [];
    private Win32Interop.LowLevelHookProc? hookProc;
    private IntPtr hookId;
    private Func<int, bool, bool, bool, bool>? handleKeyEvent;
    private Thread? hookThread;
    private uint hookThreadId;

    // The hook runs on its own message-pump thread rather than the UI thread.
    // Low-level hook callbacks are delivered through the installing thread's
    // message loop, so a UI-thread hook stalls every keystroke system-wide
    // whenever the UI is busy — and Windows silently removes hooks that exceed
    // its LowLevelHooksTimeout. A dedicated thread keeps chord handling
    // responsive no matter what the windows are doing.
    public bool Start(Func<int, bool, bool, bool, bool> handler)
    {
        handleKeyEvent = handler;
        using var hookInstalled = new ManualResetEventSlim();
        hookThread = new Thread(() =>
        {
            hookProc = HookCallback;
            using (var process = Process.GetCurrentProcess())
            using (var module = process.MainModule)
            {
                hookId = Win32Interop.SetWindowsHookEx(
                    WhKeyboardLl,
                    hookProc,
                    Win32Interop.GetModuleHandle(module?.ModuleName),
                    0);
            }

            hookThreadId = Win32Interop.GetCurrentThreadId();
            // Touch the message queue so it exists before Dispose can post
            // WM_QUIT, then let Start observe the install result.
            Win32Interop.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            hookInstalled.Set();
            if (hookId == IntPtr.Zero)
            {
                return;
            }

            while (Win32Interop.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                Win32Interop.TranslateMessage(ref message);
                Win32Interop.DispatchMessage(ref message);
            }

            Win32Interop.UnhookWindowsHookEx(hookId);
            hookId = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "Zetl keyboard hook"
        };
        // Clipboard and shell interop behave best from an STA pump thread.
        if (OperatingSystem.IsWindows())
        {
            hookThread.SetApartmentState(ApartmentState.STA);
        }

        hookThread.Start();
        hookInstalled.Wait();
        return hookId != IntPtr.Zero;
    }

    public Task<bool> SendChord(
        int vkCode,
        bool includeShift,
        bool restoreCtrl,
        bool restoreShift)
    {
        return AvaloniaWindowsInput.SendCtrlChord(
            vkCode,
            includeShift,
            restoreCtrl,
            restoreShift,
            log);
    }

    public Task<bool> SendPaste()
    {
        return AvaloniaWindowsInput.SendCtrlChord(
            ChordlKeys.VK_V,
            includeShift: false,
            restoreCtrl: false,
            restoreShift: false,
            log);
    }

    public void Dispose()
    {
        if (hookThread is { IsAlive: true })
        {
            Win32Interop.PostThreadMessage(
                hookThreadId,
                Win32Interop.WmQuit,
                UIntPtr.Zero,
                IntPtr.Zero);
            hookThread.Join(TimeSpan.FromSeconds(1));
        }

        hookThread = null;
        if (hookId != IntPtr.Zero)
        {
            // The loop normally unhooks on its way out; this is the fallback
            // when the thread never started its loop or failed to exit in time.
            Win32Interop.UnhookWindowsHookEx(hookId);
            hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int code, IntPtr messagePointer, IntPtr dataPointer)
    {
        try
        {
            return HookCallbackCore(code, messagePointer, dataPointer);
        }
        catch (Exception ex)
        {
            return ZetlCallbackSafety.FailOpen(
                ex,
                () => Win32Interop.CallNextHookEx(hookId, code, messagePointer, dataPointer),
                failure =>
                {
                    downKeys.Clear();
                    log($"Keyboard hook callback failed open: {failure.Message}");
                });
        }
    }

    private IntPtr HookCallbackCore(int code, IntPtr messagePointer, IntPtr dataPointer)
    {
        if (code < 0)
        {
            return Win32Interop.CallNextHookEx(hookId, code, messagePointer, dataPointer);
        }

        var hook = Marshal.PtrToStructure<KeyboardHook>(dataPointer);
        var isInjected = (hook.Flags & LlkInjected) != 0;
        if (isInjected
            && (!allowInjectedInputForTesting
                || hook.ExtraInfo == AvaloniaWindowsInput.SyntheticInputMarker))
        {
            return Win32Interop.CallNextHookEx(hookId, code, messagePointer, dataPointer);
        }

        var message = messagePointer.ToInt32();
        var keyDown = message is WmKeyDown or WmSysKeyDown;
        var keyUp = message is WmKeyUp or WmSysKeyUp;
        var virtualKey = (int)hook.VirtualKey;
        // A key-down for a key already held is a hardware auto-repeat. Injected
        // events are filtered above, so downKeys tracks only physical keys.
        var isRepeat = false;
        if (keyDown)
        {
            isRepeat = !downKeys.Add(virtualKey);
        }
        else if (keyUp)
        {
            downKeys.Remove(virtualKey);
        }

        if (handleKeyEvent?.Invoke(virtualKey, keyDown, keyUp, isRepeat) == true)
        {
            return (IntPtr)1;
        }

        return Win32Interop.CallNextHookEx(hookId, code, messagePointer, dataPointer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHook
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
}

internal static class AvaloniaWindowsInput
{
    private const int InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private static readonly BlockingCollection<Action> ReplayQueue = [];
    internal static readonly UIntPtr SyntheticInputMarker = new(0x5A45544C);

    static AvaloniaWindowsInput()
    {
        var thread = new Thread(ProcessReplayQueue)
        {
            IsBackground = true,
            Name = "Zetl Windows synthetic input"
        };
        thread.Start();
    }

    // Returns a task that completes with the real SendInput result once the
    // queued work runs on the replay thread -- not when it is merely queued -- so
    // callers can await actual injection success before consuming replay notes.
    public static Task<bool> SendCtrlChord(
        int virtualKey,
        bool includeShift,
        bool restoreCtrl,
        bool restoreShift,
        Action<string> log)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            ReplayQueue.Add(() =>
            {
                bool sent;
                try
                {
                    sent = SendCtrlChordNow(virtualKey, includeShift, log);
                }
                catch (Exception ex)
                {
                    log($"Synthetic input failed: {ex.Message}.");
                    sent = false;
                }

                completion.TrySetResult(sent);
            });
        }
        catch (InvalidOperationException ex)
        {
            log($"Synthetic input could not be queued: {ex.Message}.");
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    private static void ProcessReplayQueue()
    {
        foreach (var action in ReplayQueue.GetConsumingEnumerable())
        {
            action();
        }
    }

    private static bool SendCtrlChordNow(
        int virtualKey,
        bool includeShift,
        Action<string> log)
    {
        var modifiers = ModifierSnapshot.Capture();
        var sequence = ZetlChordInjection.BuildCtrlChord(
            virtualKey,
            includeShift,
            modifiers.LeftCtrl,
            modifiers.RightCtrl,
            modifiers.LeftShift,
            modifiers.RightShift);
        var inputs = new Input[sequence.Count];
        for (var i = 0; i < sequence.Count; i++)
        {
            inputs[i] = new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = (ushort)sequence[i].VirtualKey,
                        Flags = sequence[i].KeyUp ? KeyEventKeyUp : 0,
                        ExtraInfo = SyntheticInputMarker
                    }
                }
            };
        }

        Marshal.SetLastPInvokeError(0);
        var sent = SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<Input>());
        if (sent == (uint)inputs.Length)
        {
            return true;
        }

        log($"Synthetic input sent {sent}/{inputs.Length} events. Error: {Marshal.GetLastPInvokeError()}.");
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        Input[] inputs,
        int inputSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private readonly record struct ModifierSnapshot(
        bool LeftCtrl,
        bool RightCtrl,
        bool LeftShift,
        bool RightShift)
    {
        public static ModifierSnapshot Capture()
        {
            return new ModifierSnapshot(
                IsDown(ChordlKeys.VK_LCONTROL),
                IsDown(ChordlKeys.VK_RCONTROL),
                IsDown(ChordlKeys.VK_LSHIFT),
                IsDown(ChordlKeys.VK_RSHIFT));
        }

        private static bool IsDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
}

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
            var handle = GetClipboardData(UnicodeText);
            if (handle == IntPtr.Zero)
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
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public string? TryGetHtml()
    {
        if (!TryOpen())
        {
            return null;
        }

        try
        {
            var bytes = ReadClipboardBytes(Html);
            return bytes is { Length: <= MaxStoredRichHtmlBytes }
                && TryExtractHtmlFragment(bytes, out var fragment)
                    ? fragment
                    : null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public IReadOnlyList<ZetlClipboardFormatData>? TryGetReplayFormats()
    {
        if (!TryOpen())
        {
            return null;
        }

        var formats = new List<ZetlClipboardFormatData>();
        var hasNativeEmbed = false;
        var hasUnicodeText = false;
        var totalBytes = 0;
        try
        {
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
        }
        finally
        {
            CloseClipboard();
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
            var pngBytes = ReadClipboardBytes(Png);
            if (pngBytes is not null && TryNormalizeImage(pngBytes, out var pngImage))
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
        var hasEnterpriseProtectionMarker = false;
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

                // Windows Information Protection exposes this registered format
                // as system-managed metadata, often with no data handle at all.
                // Its value must be queried through EdpGetEnterpriseIdForClipboard
                // rather than GetClipboardData/GlobalLock.
                if (format == EnterpriseDataProtection)
                {
                    hasEnterpriseProtectionMarker = true;
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

        if (hasEnterpriseProtectionMarker)
        {
            if (!TryGetClipboardEnterpriseId(out var enterpriseId, out var failureReason))
            {
                return ZetlClipboardBackup.Incomplete(failureReason);
            }
            if (!string.IsNullOrEmpty(enterpriseId))
            {
                return ZetlClipboardBackup.Incomplete(
                    "the clipboard contains Windows-protected enterprise data");
            }
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
        if (!backup.IsComplete || backup.RawFormats is null)
        {
            log("Clipboard restore refused: the backup is incomplete or belongs to another backend.");
            return false;
        }

        if (!EnsureOwnerWindow())
        {
            log("Clipboard restore skipped: no owner window available; clipboard left intact.");
            return false;
        }

        var allocations = new List<(uint Format, IntPtr Handle, bool IsEnhancedMetafile)>();
        var opened = false;
        try
        {
            var seenFormats = new HashSet<uint>();
            foreach (var item in backup.RawFormats)
            {
                var format = ResolveStoredFormat(item);
                if (format == 0
                    || !seenFormats.Add(format)
                    || item.Data.Length == 0
                    || UsesNonMemoryHandle(format))
                {
                    log($"Clipboard restore refused: invalid stored clipboard format backup payload.");
                    return false;
                }

                var isEnhancedMetafile = format == EnhancedMetafile;
                var handle = isEnhancedMetafile
                    ? SetEnhMetaFileBits((uint)item.Data.Length, item.Data)
                    : AllocateGlobal(item.Data);
                if (handle == IntPtr.Zero)
                {
                    log($"Clipboard restore failed: could not allocate {DescribeFormat(format)}.");
                    return false;
                }
                allocations.Add((format, handle, isEnhancedMetafile));
            }

            if (!TryOpen())
            {
                return false;
            }
            opened = true;

            if (!EmptyClipboard())
            {
                log($"Clipboard restore failed: EmptyClipboard error {Marshal.GetLastWin32Error()}.");
                return false;
            }

            for (var index = 0; index < allocations.Count; index++)
            {
                var item = allocations[index];
                if (SetClipboardData(item.Format, item.Handle) == IntPtr.Zero)
                {
                    log($"Clipboard restore failed while writing {DescribeFormat(item.Format)} (error {Marshal.GetLastWin32Error()}).");
                    return false;
                }

                // Windows owns a successfully transferred handle.
                allocations[index] = (item.Format, IntPtr.Zero, item.IsEnhancedMetafile);
            }

            return true;
        }
        finally
        {
            foreach (var item in allocations)
            {
                if (item.Handle != IntPtr.Zero)
                {
                    if (item.IsEnhancedMetafile)
                    {
                        DeleteEnhMetaFile(item.Handle);
                    }
                    else
                    {
                        GlobalFree(item.Handle);
                    }
                }
            }
            if (opened)
            {
                CloseClipboard();
            }
        }
    }

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

    private static bool TryGetClipboardEnterpriseId(
        out string? enterpriseId,
        out string failureReason)
    {
        enterpriseId = null;
        failureReason = "";
        IntPtr value = IntPtr.Zero;
        try
        {
            var result = EdpGetEnterpriseIdForClipboard(out value);
            if (result < 0)
            {
                failureReason =
                    $"Windows clipboard protection metadata could not be verified (HRESULT 0x{result:X8})";
                return false;
            }

            enterpriseId = value == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUni(value);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            failureReason = "Windows clipboard protection metadata APIs are unavailable";
            return false;
        }
        finally
        {
            if (value != IntPtr.Zero)
            {
                HeapFree(GetProcessHeap(), 0, value);
            }
        }
    }

    public bool SetText(string text)
    {
        // Never open+empty the clipboard with a NULL owner -- that is the
        // documented destructive failure mode (EmptyClipboard sets the owner to
        // NULL and SetClipboardData then fails, leaving the clipboard cleared).
        // Refuse the write instead, so a missing owner can't wipe the clipboard.
        if (!EnsureOwnerWindow())
        {
            log("Clipboard write skipped: no owner window available; clipboard left intact.");
            return false;
        }

        if (!TryOpen())
        {
            return false;
        }

        var handle = IntPtr.Zero;
        var ownsHandle = false;
        try
        {
            // Allocate and populate the global memory BEFORE emptying the
            // clipboard. The previous order emptied first, so a later allocation
            // failure left the clipboard cleared with nothing put back.
            handle = AllocateUnicodeText(text);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            ownsHandle = true;
            if (!EmptyClipboard())
            {
                log($"Clipboard write failed: EmptyClipboard error {Marshal.GetLastWin32Error()}.");
                return false;
            }

            if (SetClipboardData(UnicodeText, handle) == IntPtr.Zero)
            {
                log($"Clipboard write failed: SetClipboardData error {Marshal.GetLastWin32Error()}.");
                return false;
            }

            // Ownership of the memory transferred to the clipboard; don't free it.
            ownsHandle = false;
            return true;
        }
        finally
        {
            if (ownsHandle && handle != IntPtr.Zero)
            {
                GlobalFree(handle);
            }

            CloseClipboard();
        }
    }

    public bool SetRichText(string plainText, string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return SetText(plainText);
        }

        if (!EnsureOwnerWindow())
        {
            log("Rich clipboard write skipped: no owner window available; clipboard left intact.");
            return false;
        }

        var textHandle = AllocateUnicodeText(plainText);
        var htmlHandle = AllocateGlobal(BuildHtmlClipboardBytes(html));
        if (textHandle == IntPtr.Zero || htmlHandle == IntPtr.Zero)
        {
            if (textHandle != IntPtr.Zero) GlobalFree(textHandle);
            if (htmlHandle != IntPtr.Zero) GlobalFree(htmlHandle);
            return false;
        }

        if (!TryOpen())
        {
            GlobalFree(textHandle);
            GlobalFree(htmlHandle);
            return false;
        }

        var ownsText = true;
        var ownsHtml = true;
        try
        {
            if (!EmptyClipboard())
            {
                log($"Rich clipboard write failed: EmptyClipboard error {Marshal.GetLastWin32Error()}.");
                return false;
            }

            var textWritten = SetClipboardData(UnicodeText, textHandle) != IntPtr.Zero;
            ownsText = !textWritten;
            var htmlWritten = SetClipboardData(Html, htmlHandle) != IntPtr.Zero;
            ownsHtml = !htmlWritten;
            if (!textWritten)
            {
                log($"Rich clipboard write failed: SetClipboardData text error {Marshal.GetLastWin32Error()}.");
            }
            if (!htmlWritten)
            {
                log($"Rich clipboard write degraded: SetClipboardData HTML error {Marshal.GetLastWin32Error()}.");
            }

            return textWritten;
        }
        finally
        {
            if (ownsText) GlobalFree(textHandle);
            if (ownsHtml) GlobalFree(htmlHandle);
            CloseClipboard();
        }
    }

    public bool SetImage(ZetlClipboardImage image)
    {
        if (!EnsureOwnerWindow())
        {
            log("Clipboard image write skipped: no owner window available; clipboard left intact.");
            return false;
        }

        byte[] dib;
        try
        {
            dib = CreateDib(image.PngBytes);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            log($"Clipboard image write failed: {ex.Message}");
            return false;
        }

        var pngHandle = AllocateGlobal(image.PngBytes);
        var dibHandle = AllocateGlobal(dib);
        if (pngHandle == IntPtr.Zero || dibHandle == IntPtr.Zero)
        {
            if (pngHandle != IntPtr.Zero) GlobalFree(pngHandle);
            if (dibHandle != IntPtr.Zero) GlobalFree(dibHandle);
            return false;
        }

        if (!TryOpen())
        {
            GlobalFree(pngHandle);
            GlobalFree(dibHandle);
            return false;
        }

        var ownsPng = true;
        var ownsDib = true;
        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            var pngWritten = SetClipboardData(Png, pngHandle) != IntPtr.Zero;
            ownsPng = !pngWritten;
            var dibWritten = SetClipboardData(Dib, dibHandle) != IntPtr.Zero;
            ownsDib = !dibWritten;
            if (!pngWritten && !dibWritten)
            {
                log($"Clipboard image write failed: SetClipboardData error {Marshal.GetLastWin32Error()}.");
            }

            return pngWritten || dibWritten;
        }
        finally
        {
            if (ownsPng) GlobalFree(pngHandle);
            if (ownsDib) GlobalFree(dibHandle);
            CloseClipboard();
        }
    }

    private IntPtr AllocateUnicodeText(string text)
    {
        int bytes;
        try
        {
            bytes = checked((text.Length + 1) * sizeof(char));
        }
        catch (OverflowException)
        {
            log("Clipboard write failed: text is too large.");
            return IntPtr.Zero;
        }

        var handle = GlobalAlloc(Moveable, (UIntPtr)bytes);
        if (handle == IntPtr.Zero)
        {
            log($"Clipboard write failed: could not allocate {bytes} bytes.");
            return IntPtr.Zero;
        }

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            log("Clipboard write failed: could not lock global memory.");
            GlobalFree(handle);
            return IntPtr.Zero;
        }

        try
        {
            Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
            Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
            return handle;
        }
        finally
        {
            GlobalUnlock(handle);
        }
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

    private bool TryOpen()
    {
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

        log($"Windows clipboard was temporarily unavailable (error {Marshal.GetLastWin32Error()}).");
        return false;
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

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

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

    [DllImport("edputil.dll")]
    private static extern int EdpGetEnterpriseIdForClipboard(out IntPtr enterpriseId);

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
    private static extern IntPtr GetProcessHeap();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool HeapFree(IntPtr heap, uint flags, IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);
}

internal sealed class UnsupportedKeyboardBackend(Action<string> log) : IKeyboardBackend
{
    public bool Start(Func<int, bool, bool, bool, bool> handleKeyEvent)
    {
        log("Global shortcuts are unavailable: no platform keyboard backend is installed.");
        return false;
    }

    public Task<bool> SendChord(
        int vkCode,
        bool includeShift,
        bool restoreCtrl,
        bool restoreShift) => Task.FromResult(false);

    public Task<bool> SendPaste() => Task.FromResult(false);

    public void Dispose()
    {
    }
}

internal sealed class UnsupportedClipboard(Action<string> log) : IClipboard
{
    public string? TryGetText() => null;

    public string? TryGetHtml() => null;

    public IReadOnlyList<ZetlClipboardFormatData>? TryGetReplayFormats() => null;

    public ZetlClipboardImage? TryGetImage() => null;

    public bool SetImage(ZetlClipboardImage image)
    {
        log("Clipboard image write ignored: no platform clipboard backend is installed.");
        return false;
    }

    public bool SetText(string text)
    {
        log("Clipboard write ignored: no platform clipboard backend is installed.");
        return false;
    }

    public bool SetRichText(string plainText, string html)
    {
        log("Rich clipboard write ignored: no platform clipboard backend is installed.");
        return false;
    }

    public ZetlClipboardBackup CaptureBackup() =>
        ZetlClipboardBackup.Incomplete("no platform clipboard backend is installed");

    public bool RestoreBackup(ZetlClipboardBackup backup)
    {
        log("Clipboard restore ignored: no platform clipboard backend is installed.");
        return false;
    }

    public uint GetChangeToken() => 0;
}

internal static class ZetlForegroundService
{
    // Zetl no longer touches the system-wide foreground-lock timeout. Focus is
    // taken with targeted AttachThreadInput activation (ZetlWindowActivation and
    // RestoreTarget), which works for a background process without altering a
    // global Windows setting.

    public static object? CaptureTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = Win32Interop.GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var threadId = Win32Interop.GetWindowThreadProcessId(handle, out var processId);
        return threadId == 0 || processId == 0
            ? null
            : new WindowsForegroundTarget(handle, processId);
    }

    public static void RestoreTarget(object? target, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (target is not WindowsForegroundTarget windowsTarget
            || windowsTarget.Handle == IntPtr.Zero
            || !Win32Interop.IsWindow(windowsTarget.Handle)
            || !TargetProcessStillMatches(windowsTarget))
        {
            log?.Invoke($"Restore skipped for target {DescribeTarget(target)}.");
            return;
        }

        RestoreWindowsTarget(windowsTarget.Handle, log);
    }

    public static IntPtr? GetWindowsHandle(object? target)
    {
        return target is WindowsForegroundTarget windowsTarget
            ? windowsTarget.Handle
            : null;
    }

    public static string DescribeTarget(object? target)
    {
        if (target is not WindowsForegroundTarget windowsTarget)
        {
            return "none";
        }

        return $"hwnd=0x{windowsTarget.Handle.ToInt64():X}, pid={windowsTarget.ProcessId}";
    }

    private static void RestoreWindowsTarget(IntPtr handle, Action<string>? log = null)
    {
        var currentForeground = Win32Interop.GetForegroundWindow();
        if (currentForeground == handle)
        {
            log?.Invoke($"Restore: target 0x{handle.ToInt64():X} already foreground.");
            return;
        }

        // Attach our own (calling) thread to the thread that currently owns the
        // foreground, mirroring the window-open activation path. Without this,
        // SetForegroundWindow is issued from an unattached thread and Windows
        // returns true but ignores it once the popup is no longer foreground —
        // dropping focus onto whatever Windows picked instead.
        var foregroundThreadId = currentForeground == IntPtr.Zero
            ? 0
            : Win32Interop.GetWindowThreadProcessId(currentForeground, out _);
        var currentThreadId = Win32Interop.GetCurrentThreadId();
        var attached = foregroundThreadId != 0
            && foregroundThreadId != currentThreadId
            && Win32Interop.AttachThreadInput(
                currentThreadId,
                foregroundThreadId,
                attach: true);
        bool set;
        try
        {
            set = Win32Interop.SetForegroundWindow(handle);
        }
        finally
        {
            if (attached)
            {
                Win32Interop.AttachThreadInput(
                    currentThreadId,
                    foregroundThreadId,
                    attach: false);
            }
        }

        log?.Invoke(
            $"Restore: target=0x{handle.ToInt64():X}, "
            + $"before=0x{currentForeground.ToInt64():X}, "
            + $"set={set}, "
            + $"after=0x{Win32Interop.GetForegroundWindow().ToInt64():X}.");
    }

    private static bool TargetProcessStillMatches(
        WindowsForegroundTarget target)
    {
        Win32Interop.GetWindowThreadProcessId(target.Handle, out var processId);
        return processId == target.ProcessId;
    }

    private readonly record struct WindowsForegroundTarget(
        IntPtr Handle,
        uint ProcessId);
}
