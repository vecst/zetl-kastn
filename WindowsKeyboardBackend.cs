using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZETL;

/// <summary>
/// Windows <see cref="IKeyboardBackend"/>: a WH_KEYBOARD_LL low-level hook for
/// interception and <see cref="ChordlInput"/> (SendInput) for synthetic replay.
/// Injected events are skipped so Zetl's own replays don't re-enter the hook.
/// </summary>
internal sealed class WindowsKeyboardBackend : IKeyboardBackend
{
    private const int WH_KEYBOARD_LL = 13;
    private const int LLKHF_INJECTED = 0x10;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly Action<string> log;
    private readonly HashSet<int> downKeys = [];
    private LowLevelKeyboardProc? hookProc; // kept referenced so it isn't collected
    private IntPtr hookId = IntPtr.Zero;
    private Func<int, bool, bool, bool, bool>? handleKeyEvent;

    public WindowsKeyboardBackend(Action<string> log)
    {
        this.log = log;
    }

    public bool Start(Func<int, bool, bool, bool, bool> handleKeyEvent)
    {
        this.handleKeyEvent = handleKeyEvent;
        hookProc = HookCallback;
        hookId = SetHook(hookProc);
        return hookId != IntPtr.Zero;
    }

    public bool SendChord(int vkCode, bool includeShift, bool restoreCtrl, bool restoreShift)
    {
        return ChordlInput.SendCtrlChord(vkCode, includeShift, restoreCtrl, restoreShift, log);
    }

    public bool SendPaste()
    {
        return ChordlInput.SendPaste(log);
    }

    public void Dispose()
    {
        if (hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hookId);
            hookId = IntPtr.Zero;
        }
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(currentModule?.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }

        var hook = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if ((hook.flags & LLKHF_INJECTED) != 0)
        {
            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }

        var vkCode = (int)hook.vkCode;
        var message = wParam.ToInt32();
        var isKeyDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var isKeyUp = message is WM_KEYUP or WM_SYSKEYUP;
        // A key-down for a key already held is a hardware auto-repeat. Injected
        // events are filtered above, so downKeys tracks only physical keys.
        var isRepeat = false;
        if (isKeyDown)
        {
            isRepeat = !downKeys.Add(vkCode);
        }
        else if (isKeyUp)
        {
            downKeys.Remove(vkCode);
        }

        if (handleKeyEvent?.Invoke(vkCode, isKeyDown, isKeyUp, isRepeat) == true)
        {
            return (IntPtr)1;
        }

        return CallNextHookEx(hookId, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
