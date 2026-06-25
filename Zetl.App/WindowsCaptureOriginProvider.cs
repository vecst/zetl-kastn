using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ZETL;

internal sealed class WindowsCaptureOriginProvider : ICaptureOriginProvider
{
    public ZetlCaptureOrigin? Capture(string detail)
    {
        if (!OperatingSystem.IsWindows()
            || !ZetlCaptureOriginDetail.IncludesApplication(detail))
        {
            return null;
        }

        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero
            || NativeMethods.GetWindowThreadProcessId(window, out var processId) == 0
            || processId == 0)
        {
            return null;
        }

        var processName = "";
        var applicationName = "";
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            processName = process.ProcessName;
            try
            {
                applicationName = process.MainModule?.FileVersionInfo.FileDescription ?? "";
            }
            catch (Exception ex) when (
                ex is InvalidOperationException
                    or NotSupportedException
                    or System.ComponentModel.Win32Exception)
            {
                // Elevated and packaged apps may deny module metadata. The
                // process name still gives the capture a useful stable origin.
            }
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(applicationName))
        {
            applicationName = processName;
        }

        var windowTitle = ZetlCaptureOriginDetail.IncludesWindowTitle(detail)
            ? ReadWindowTitle(window)
            : null;
        return ZetlCaptureOrigin.Create(
            applicationName,
            processName,
            windowTitle,
            detail);
    }

    private static string? ReadWindowTitle(IntPtr window)
    {
        var length = NativeMethods.GetWindowTextLength(window);
        if (length <= 0)
        {
            return null;
        }

        var buffer = new StringBuilder(length + 1);
        return NativeMethods.GetWindowText(window, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : null;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(
            IntPtr window,
            out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(
            IntPtr window,
            StringBuilder text,
            int maxCount);
    }
}
