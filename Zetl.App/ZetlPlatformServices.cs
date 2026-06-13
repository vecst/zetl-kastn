using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Chordl;

namespace ZETL;

internal static class ZetlPlatformServices
{
    public static IKeyboardBackend CreateKeyboard(Action<string> log)
    {
        return OperatingSystem.IsWindows()
            ? new AvaloniaWindowsKeyboardBackend(log)
            : new UnsupportedKeyboardBackend(log);
    }

    public static IClipboard CreateClipboard(Action<string> log)
    {
        return OperatingSystem.IsWindows()
            ? new AvaloniaWindowsClipboard(log)
            : new UnsupportedClipboard(log);
    }
}

internal sealed class AvaloniaWindowsKeyboardBackend(Action<string> log) : IKeyboardBackend
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

    public bool SendChord(
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

    public bool SendPaste()
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
        if ((hook.Flags & LlkInjected) != 0)
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

    static AvaloniaWindowsInput()
    {
        var thread = new Thread(ProcessReplayQueue)
        {
            IsBackground = true,
            Name = "Zetl Windows synthetic input"
        };
        thread.Start();
    }

    public static bool SendCtrlChord(
        int virtualKey,
        bool includeShift,
        bool restoreCtrl,
        bool restoreShift,
        Action<string> log)
    {
        try
        {
            ReplayQueue.Add(() =>
            {
                try
                {
                    SendCtrlChordNow(
                        virtualKey,
                        includeShift,
                        log);
                }
                catch (Exception ex)
                {
                    log($"Synthetic input failed: {ex.Message}.");
                }
            });
            return true;
        }
        catch (InvalidOperationException ex)
        {
            log($"Synthetic input could not be queued: {ex.Message}.");
            return false;
        }
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
                        Flags = sequence[i].KeyUp ? KeyEventKeyUp : 0
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
    private const uint Moveable = 0x0002;
    private const int ClipboardAttempts = 5;
    private const int HwndMessage = -3;

    private readonly Action<string> log;
    private IntPtr ownerWindow;

    public AvaloniaWindowsClipboard(Action<string> log)
    {
        this.log = log;
        ownerWindow = CreateOwnerWindow(log);
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

    public bool SetText(string text)
    {
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
            var bytes = checked((text.Length + 1) * sizeof(char));
            handle = GlobalAlloc(Moveable, (UIntPtr)bytes);
            if (handle == IntPtr.Zero)
            {
                log($"Clipboard write failed: could not allocate {bytes} bytes.");
                return false;
            }

            ownsHandle = true;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                log("Clipboard write failed: could not lock global memory.");
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(handle);
            }

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

    public uint GetChangeToken()
    {
        return GetClipboardSequenceNumber();
    }

    public void Dispose()
    {
        if (ownerWindow != IntPtr.Zero)
        {
            DestroyWindow(ownerWindow);
            ownerWindow = IntPtr.Zero;
        }
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
    // and pump-light. Falls back to a NULL owner if creation fails.
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
            log($"Clipboard owner window unavailable (error {Marshal.GetLastWin32Error()}); using the default association.");
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
    private static extern IntPtr GlobalFree(IntPtr memory);
}

internal sealed class UnsupportedKeyboardBackend(Action<string> log) : IKeyboardBackend
{
    public bool Start(Func<int, bool, bool, bool, bool> handleKeyEvent)
    {
        log("Global shortcuts are unavailable: no platform keyboard backend is installed.");
        return false;
    }

    public bool SendChord(
        int vkCode,
        bool includeShift,
        bool restoreCtrl,
        bool restoreShift) => false;

    public bool SendPaste() => false;

    public void Dispose()
    {
    }
}

internal sealed class UnsupportedClipboard(Action<string> log) : IClipboard
{
    public string? TryGetText() => null;

    public bool SetText(string text)
    {
        log("Clipboard write ignored: no platform clipboard backend is installed.");
        return false;
    }

    public uint GetChangeToken() => 0;
}

internal static class ZetlForegroundService
{
    // A background process is denied SetForegroundWindow until it has received
    // genuine user input, so the first popup steals focus only partially and
    // restores it to the wrong window — until a real click resets the lock for
    // the session. Clearing the foreground-lock timeout at startup puts the
    // process in that "allowed" state from the very first popup.
    public static bool AllowForegroundActivation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return Win32Interop.SystemParametersInfo(
            Win32Interop.SPI_SETFOREGROUNDLOCKTIMEOUT,
            0,
            IntPtr.Zero,
            0);
    }

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
