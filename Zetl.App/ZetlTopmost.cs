using Avalonia.Controls;

namespace ZETL;

// Puts a reused always-on-top window (the toast, the hold indicator) back on
// top each time it shows, without taking focus. Avalonia only acts when the
// Topmost property changes, which misses both platforms' ways of losing it:
// on Windows another app can claim the top of the topmost band since, and on
// X11 the window manager drops "keep above" when a window hides, so the next
// show maps an ordinary window, which a window that doesn't take focus lands
// behind the app in use (seen on Cinnamon).
internal static class ZetlTopmost
{
    public static void Reassert(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            window.Topmost = false;
            window.Topmost = true;
            return;
        }

        if (window.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero)
        {
            return;
        }

        // SWP_NOACTIVATE keeps re-topping from stealing focus.
        Win32Interop.SetWindowPos(
            handle,
            Win32Interop.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);
    }
}
