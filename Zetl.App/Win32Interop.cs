using System.Runtime.InteropServices;

namespace ZETL;

// Shared Win32 P/Invoke surface for the Avalonia head. Centralizes the
// user32/kernel32 entry points that were otherwise redeclared across the
// keyboard hook, the foreground/activation helper, and the click-away monitor.
// Single-use imports (clipboard, SendInput, the mouse-hook-only helpers) stay
// local to their owners.
internal static class Win32Interop
{
    // Low-level hook callback. The keyboard (WH_KEYBOARD_LL) and mouse
    // (WH_MOUSE_LL) hooks share this signature.
    internal delegate IntPtr LowLevelHookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelHookProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll")]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    internal static extern bool AttachThreadInput(
        uint currentThreadId,
        uint targetThreadId,
        bool attach);

    internal const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SystemParametersInfo(
        uint action,
        uint param,
        IntPtr pvParam,
        uint winIni);
}
