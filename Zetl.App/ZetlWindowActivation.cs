using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace ZETL;

internal static class ZetlWindowActivation
{
    public static void Show(
        Window window,
        Window? owner = null,
        object? activationTarget = null)
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
        window.Activate();
        if (OperatingSystem.IsWindows()
            && window.TryGetPlatformHandle()?.Handle is { } handle
            && handle != IntPtr.Zero)
        {
            BringToForeground(
                window,
                handle,
                activationTarget is IntPtr target ? target : IntPtr.Zero);
        }
    }

    private static void BringToForeground(
        Window window,
        IntPtr windowHandle,
        IntPtr preferredForegroundWindow)
    {
        var foregroundWindow = preferredForegroundWindow != IntPtr.Zero
            ? preferredForegroundWindow
            : GetForegroundWindow();
        var foregroundThreadId = foregroundWindow == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, out _);
        var currentThreadId = GetCurrentThreadId();
        var attached = foregroundThreadId != 0
            && foregroundThreadId != currentThreadId
            && AttachThreadInput(
                currentThreadId,
                foregroundThreadId,
                attach: true);

        window.Topmost = true;
        try
        {
            BringWindowToTop(windowHandle);
            SetForegroundWindow(windowHandle);
            SetActiveWindow(windowHandle);
            window.Activate();
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(
                    currentThreadId,
                    foregroundThreadId,
                    attach: false);
            }
        }

        var releaseTopmost = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        releaseTopmost.Tick += (_, _) =>
        {
            releaseTopmost.Stop();
            if (window.IsVisible)
            {
                window.Topmost = false;
                window.Activate();
            }
        };
        releaseTopmost.Start();
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

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr window);
}
