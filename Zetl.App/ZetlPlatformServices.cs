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
    private const uint UnicodeText = 13;
    private const uint Dib = 8;
    private const uint DibV5 = 17;
    private const uint Moveable = 0x0002;
    private const int ClipboardAttempts = 5;
    private const int HwndMessage = -3;

    private readonly Action<string> log;
    private readonly Func<IntPtr> createOwnerWindow;
    private IntPtr ownerWindow;
    private static readonly uint Png = RegisterClipboardFormat("PNG");
    private static readonly uint Html = RegisterClipboardFormat("HTML Format");

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
