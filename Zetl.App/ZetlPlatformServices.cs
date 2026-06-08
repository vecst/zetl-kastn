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
    private LowLevelKeyboardProc? hookProc;
    private IntPtr hookId;
    private Func<int, bool, bool, bool>? handleKeyEvent;

    public bool Start(Func<int, bool, bool, bool> handler)
    {
        handleKeyEvent = handler;
        hookProc = HookCallback;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        hookId = SetWindowsHookEx(
            WhKeyboardLl,
            hookProc,
            GetModuleHandle(module?.ModuleName),
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
            UnhookWindowsHookEx(hookId);
            hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int code, IntPtr messagePointer, IntPtr dataPointer)
    {
        if (code < 0)
        {
            return CallNextHookEx(hookId, code, messagePointer, dataPointer);
        }

        var hook = Marshal.PtrToStructure<KeyboardHook>(dataPointer);
        if ((hook.Flags & LlkInjected) != 0)
        {
            return CallNextHookEx(hookId, code, messagePointer, dataPointer);
        }

        var message = messagePointer.ToInt32();
        var keyDown = message is WmKeyDown or WmSysKeyDown;
        var keyUp = message is WmKeyUp or WmSysKeyUp;
        if (handleKeyEvent?.Invoke((int)hook.VirtualKey, keyDown, keyUp) == true)
        {
            return (IntPtr)1;
        }

        return CallNextHookEx(hookId, code, messagePointer, dataPointer);
    }

    private delegate IntPtr LowLevelKeyboardProc(
        int code,
        IntPtr messagePointer,
        IntPtr dataPointer);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHook
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr messagePointer,
        IntPtr dataPointer);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
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
    public bool Start(Func<int, bool, bool, bool> handleKeyEvent)
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
    public static object? CaptureTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var threadId = GetWindowThreadProcessId(handle, out var processId);
        return threadId == 0 || processId == 0
            ? null
            : new WindowsForegroundTarget(handle, processId);
    }

    public static void RestoreTarget(object? target)
    {
        if (OperatingSystem.IsWindows()
            && target is WindowsForegroundTarget windowsTarget
            && windowsTarget.Handle != IntPtr.Zero
            && IsWindow(windowsTarget.Handle)
            && TargetProcessStillMatches(windowsTarget))
        {
            RestoreWindowsTarget(windowsTarget.Handle);
        }
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

    private static void RestoreWindowsTarget(IntPtr handle)
    {
        var currentForeground = GetForegroundWindow();
        if (currentForeground == handle)
        {
            return;
        }

        var foregroundThreadId = currentForeground == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(currentForeground, out _);
        var targetThreadId = GetWindowThreadProcessId(handle, out _);
        var attached = foregroundThreadId != 0
            && targetThreadId != 0
            && foregroundThreadId != targetThreadId
            && AttachThreadInput(
                targetThreadId,
                foregroundThreadId,
                attach: true);
        try
        {
            SetForegroundWindow(handle);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(
                    targetThreadId,
                    foregroundThreadId,
                    attach: false);
            }
        }
    }

    private static bool TargetProcessStillMatches(
        WindowsForegroundTarget target)
    {
        GetWindowThreadProcessId(target.Handle, out var processId);
        return processId == target.ProcessId;
    }

    private readonly record struct WindowsForegroundTarget(
        IntPtr Handle,
        uint ProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(
        uint currentThreadId,
        uint targetThreadId,
        bool attach);
}
