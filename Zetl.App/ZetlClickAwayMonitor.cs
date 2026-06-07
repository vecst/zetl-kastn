using System.Runtime.InteropServices;

namespace ZETL;

// A popup that should dismiss when the user clicks outside the app.
internal interface IClickAwayDismissable
{
    void DismissFromClickAway();
}

// A global low-level mouse hook that fires a callback when a left-click lands
// on a window outside Zetl's own process. Popups normally dismiss on the OS
// Deactivated event, but clicking the bare Windows desktop (Progman) does not
// move the foreground, so no Deactivated arrives; this catches that case (and
// any other click-away) directly. Windows only.
internal sealed class ZetlClickAwayMonitor : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const uint GA_ROOT = 2;

    private readonly Action onClickOutside;
    private readonly uint ownProcessId;
    private readonly LowLevelMouseProc proc;
    private IntPtr hookId = IntPtr.Zero;

    public ZetlClickAwayMonitor(Action onClickOutside)
    {
        this.onClickOutside = onClickOutside;
        ownProcessId = (uint)Environment.ProcessId;
        proc = HookCallback;
        if (OperatingSystem.IsWindows())
        {
            hookId = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam == WM_LBUTTONDOWN)
        {
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var under = WindowFromPoint(data.pt);
                var root = under == IntPtr.Zero
                    ? IntPtr.Zero
                    : GetAncestor(under, GA_ROOT);
                if (root != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(root, out var pid);
                    if (pid != ownProcessId)
                    {
                        onClickOutside();
                    }
                }
            }
            catch
            {
                // Diagnostics must never disrupt input handling.
            }
        }

        return CallNextHookEx(hookId, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hookId);
            hookId = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);
}
