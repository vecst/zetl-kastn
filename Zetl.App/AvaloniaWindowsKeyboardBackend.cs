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
