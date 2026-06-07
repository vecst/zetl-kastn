using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ZETL;

internal static class ZetlWindowActivation
{
    public static void Show(
        Window window,
        Window? owner = null,
        object? activationTarget = null,
        Action<string>? log = null)
    {
        if (!window.IsVisible)
        {
            if (owner is not null)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }
        }

        window.WindowState = WindowState.Normal;

        if (!OperatingSystem.IsWindows())
        {
            // Other platforms (incl. Wayland) may deny activation; degrade
            // cleanly with Avalonia's own activation.
            window.Activate();
            return;
        }

        ActivateWindowsForeground(window, activationTarget, log);
    }

    private static void ActivateWindowsForeground(
        Window window,
        object? activationTarget,
        Action<string>? log)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        // The window we are taking the foreground from: the captured shortcut
        // target if we have one, otherwise whatever is foreground right now.
        var outgoing = ZetlForegroundService.GetWindowsHandle(activationTarget)
            is { } target && target != IntPtr.Zero
            ? target
            : GetForegroundWindow();

        // Force Z-order so the popup is visible even while the foreground steal
        // is contested. Topmost is an Avalonia property (SetWindowPos with
        // SWP_NOACTIVATE), so it does not perturb activation state.
        window.Topmost = true;
        TryActivate(window, outgoing);

        // Windows can deny SetForegroundWindow to a background process on the
        // first try. Retry the activation through Avalonia (never raw Win32, so
        // its focus manager stays in sync and IsActive matches reality) until
        // the window actually holds the foreground.
        var attempts = 0;
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        timer.Tick += (_, _) =>
        {
            attempts++;
            if (!window.IsVisible)
            {
                timer.Stop();
                return;
            }

            var foreground = GetForegroundWindow();
            if (foreground == handle || attempts >= 6)
            {
                window.Topmost = false;
                timer.Stop();
                log?.Invoke(
                    $"Activation settled: attempts={attempts}, "
                    + $"foreground=0x{foreground.ToInt64():X}, "
                    + $"window=0x{handle.ToInt64():X}, isActive={window.IsActive}.");
                return;
            }

            TryActivate(window, outgoing);
        };
        timer.Start();
    }

    private static void TryActivate(Window window, IntPtr outgoing)
    {
        var outgoingThreadId = outgoing == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(outgoing, out _);
        var currentThreadId = GetCurrentThreadId();
        // Attaching our input queue to the outgoing foreground thread lets a
        // background process win SetForegroundWindow. Avalonia's Activate drives
        // the actual activation so the focus manager engages.
        var attached = outgoingThreadId != 0
            && outgoingThreadId != currentThreadId
            && AttachThreadInput(currentThreadId, outgoingThreadId, attach: true);
        try
        {
            window.Activate();
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThreadId, outgoingThreadId, attach: false);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(
        uint currentThreadId,
        uint targetThreadId,
        bool attach);
}
