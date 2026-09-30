using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ZETL;

internal sealed class WindowsCaptureOriginProvider : ICaptureOriginProvider
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    // Application display names by executable path. Reading a version resource
    // touches the file, so each executable pays it once.
    private readonly ConcurrentDictionary<string, string> descriptions =
        new(StringComparer.OrdinalIgnoreCase);

    // Hook thread: the foreground window, its executable path, and its title.
    // A limited-rights process query and a caption read, microseconds each;
    // unlike enumerating the process's modules, it also works for elevated apps.
    public ZetlGestureTarget? CaptureTarget(string detail)
    {
        if (!OperatingSystem.IsWindows()
            || !ZetlCaptureOriginDetail.IncludesApplication(detail))
        {
            return null;
        }

        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero
            || NativeMethods.GetWindowThreadProcessId(window, out var processId) == 0
            || processId == 0
            || ReadImagePath(processId) is not { } imagePath)
        {
            return null;
        }

        var windowTitle = ZetlCaptureOriginDetail.IncludesWindowTitle(detail)
            ? ReadWindowTitle(window)
            : null;
        return new ZetlGestureTarget(window, processId, imagePath, windowTitle);
    }

    // Off the hook thread: names the application from its executable.
    public ZetlCaptureOrigin? Describe(ZetlGestureTarget target, string detail)
    {
        var processName = Path.GetFileNameWithoutExtension(target.ImagePath);
        var applicationName = descriptions.GetOrAdd(target.ImagePath, ReadDescription);
        return ZetlCaptureOrigin.Create(
            string.IsNullOrWhiteSpace(applicationName) ? processName : applicationName,
            processName,
            target.WindowTitle,
            detail);
    }

    private static string? ReadImagePath(uint processId)
    {
        var process = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var length = buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref length)
                ? buffer.ToString(0, length)
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    private static string ReadDescription(string imagePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(imagePath).FileDescription?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Some packaged apps keep their executable out of reach. The process
            // name still gives the capture a useful stable origin.
            return "";
        }
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

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(
            IntPtr process,
            uint flags,
            StringBuilder path,
            ref int size);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
