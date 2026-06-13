using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace ZETL;

// A popup that should dismiss when the user clicks outside the app.
internal interface IClickAwayDismissable
{
    void DismissFromClickAway();
}

// Detects click-away from open popups by polling, instead of the global
// WH_MOUSE_LL hook this replaces: that hook taxed every mouse event
// system-wide for the app's whole lifetime. The watcher runs only while a
// click-away popup is open, on the UI thread, and fires on either signal:
//
// - the foreground window changed to another process's window (a click or
//   alt-tab into another app, even when the popup never won the foreground),
// - the left button is down — or was clicked since the last tick — over a
//   window outside Zetl's process (covers clicking the bare desktop, which
//   does not move the foreground, so no Deactivated ever arrives).
//
// Popups normally dismiss on the OS Deactivated event; this catches the
// cases that event misses. Windows only.
internal sealed class ZetlClickAwayWatcher
{
    private const int VkLeftButton = 0x01;
    private const uint GaRoot = 2;

    private readonly Action onClickOutside;
    private readonly uint ownProcessId = (uint)Environment.ProcessId;
    private readonly DispatcherTimer timer;
    private IntPtr lastForeground;

    public ZetlClickAwayWatcher(Action onClickOutside)
    {
        this.onClickOutside = onClickOutside;
        timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(75)
        };
        timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || timer.IsEnabled)
        {
            return;
        }

        // Prime the transition check with whatever is foreground now, so a
        // popup that opens behind another app is not dismissed before the
        // user does anything.
        lastForeground = Win32Interop.GetForegroundWindow();
        // Reset GetAsyncKeyState's pressed-since-last-call bit so a click
        // from before the popup opened does not count as a click-away.
        GetAsyncKeyState(VkLeftButton);
        timer.Start();
    }

    public void Stop()
    {
        timer.Stop();
    }

    private void Tick()
    {
        // A drag that began in one of our windows (e.g. highlighting note text)
        // holds the mouse capture on this UI thread. While the button is held,
        // WindowFromPoint reports whatever is physically under the pointer as it
        // wanders past the popup's edge, which used to look like a click-away and
        // commit/close the note mid-selection. Capture is thread-local, so a
        // non-zero result here means a Zetl window owns the drag: never dismiss.
        if (GetCapture() != IntPtr.Zero)
        {
            return;
        }

        if (ForegroundMovedToOtherProcess() || ClickedOutsideZetl())
        {
            onClickOutside();
        }
    }

    private bool ForegroundMovedToOtherProcess()
    {
        var foreground = Win32Interop.GetForegroundWindow();
        if (foreground == lastForeground)
        {
            return false;
        }

        lastForeground = foreground;
        return foreground != IntPtr.Zero && !BelongsToZetl(foreground);
    }

    private bool ClickedOutsideZetl()
    {
        // 0x8000: button is down now. 0x0001: it was pressed at some point
        // since the last call, catching clicks faster than the tick interval.
        if ((GetAsyncKeyState(VkLeftButton) & 0x8001) == 0)
        {
            return false;
        }

        if (!GetCursorPos(out var point))
        {
            return false;
        }

        var under = WindowFromPoint(point);
        if (under == IntPtr.Zero)
        {
            return false;
        }

        var root = GetAncestor(under, GaRoot);
        return root != IntPtr.Zero && !BelongsToZetl(root);
    }

    private bool BelongsToZetl(IntPtr window)
    {
        Win32Interop.GetWindowThreadProcessId(window, out var processId);
        return processId == ownProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
}
