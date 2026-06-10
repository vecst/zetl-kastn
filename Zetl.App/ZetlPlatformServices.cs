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

    public bool Start(Func<int, bool, bool, bool, bool> handler)
    {
        handleKeyEvent = handler;
        hookProc = HookCallback;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        hookId = Win32Interop.SetWindowsHookEx(
            WhKeyboardLl,
            hookProc,
            Win32Interop.GetModuleHandle(module?.ModuleName),
            0);
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
        if (hookId != IntPtr.Zero)
        {
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

internal sealed class AvaloniaWindowsClipboard(Action<string> log) : IClipboard
{
    private const uint UnicodeText = 13;
    private const uint Moveable = 0x0002;
    private const int ClipboardAttempts = 5;

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

    public void SetText(string text)
    {
        if (!TryOpen())
        {
            return;
        }

        IntPtr handle = IntPtr.Zero;
        try
        {
            if (!EmptyClipboard())
            {
                return;
            }

            var bytes = checked((text.Length + 1) * sizeof(char));
            handle = GlobalAlloc(Moveable, (UIntPtr)bytes);
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return;
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

            if (SetClipboardData(UnicodeText, handle) != IntPtr.Zero)
            {
                handle = IntPtr.Zero;
            }
        }
        finally
        {
            if (handle != IntPtr.Zero)
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

    private bool TryOpen()
    {
        for (var attempt = 0; attempt < ClipboardAttempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            Thread.Sleep(5);
        }

        log("Windows clipboard was temporarily unavailable.");
        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

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

    public void SetText(string text)
    {
        log("Clipboard write ignored: no platform clipboard backend is installed.");
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
