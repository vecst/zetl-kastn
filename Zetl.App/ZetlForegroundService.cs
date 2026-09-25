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

internal static class ZetlForegroundService
{
    // Zetl no longer touches the system-wide foreground-lock timeout. Focus is
    // taken with targeted AttachThreadInput activation (ZetlWindowActivation and
    // RestoreTarget), which works for a background process without altering a
    // global Windows setting.

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
