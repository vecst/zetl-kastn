using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace ZETL;

internal static class ZetlWindowActivation
{
    public static void Show(Window window, Window? owner = null)
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
            SetForegroundWindow(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
