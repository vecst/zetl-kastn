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
        object? activationTarget = null,
        Action<string>? log = null)
    {
        var foregroundBeforeShow = OperatingSystem.IsWindows()
            ? GetForegroundWindow()
            : IntPtr.Zero;
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
                ZetlForegroundService.GetWindowsHandle(activationTarget)
                    ?? foregroundBeforeShow,
                log);
        }
    }

    private static void BringToForeground(
        Window window,
        IntPtr windowHandle,
        IntPtr foregroundBeforeShow,
        Action<string>? log)
    {
        var foregroundWindow = foregroundBeforeShow != IntPtr.Zero
            ? foregroundBeforeShow
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
            Interval = TimeSpan.FromMilliseconds(75)
        };
        var attempts = 0;
        releaseTopmost.Tick += (_, _) =>
        {
            attempts++;
            if (!window.IsVisible)
            {
                releaseTopmost.Stop();
                return;
            }

            var activeWindow = GetForegroundWindow();
            if (activeWindow == windowHandle || attempts >= 6)
            {
                window.Topmost = false;
                releaseTopmost.Stop();
            }

            BringWindowToTop(windowHandle);
            SetForegroundWindow(windowHandle);
            SetActiveWindow(windowHandle);
            window.Activate();
            log?.Invoke(
                $"Activation retry {attempts}: "
                + $"foreground=0x{activeWindow.ToInt64():X}, "
                + $"window=0x{windowHandle.ToInt64():X}.");
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
