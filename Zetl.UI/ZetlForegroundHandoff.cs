using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZETL;

/// <summary>
/// Lets another of the apps take the foreground for a window it raises on our
/// behalf (Kastn's quit confirmation, Kastn coming forward on "Open Kastn").
/// Windows only lets a background process take the foreground in narrow cases,
/// such as having been started by the foreground process, so whether Kastn's
/// window got focus used to depend on how Kastn was launched. The process that
/// just received the user's click passes the right on explicitly instead.
/// Call it synchronously from the input handler, while that right is ours.
/// </summary>
internal static class ZetlForegroundHandoff
{
    public static void AllowProcessesNamed(string processName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId)
                {
                    AllowSetForegroundWindow(process.Id);
                }
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
