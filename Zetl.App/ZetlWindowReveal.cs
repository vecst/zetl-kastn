using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ZETL;

// Keeps a window hidden until its first frame is drawn, then shows it all at
// once. Windows draws a window's frame immediately, but a large window like
// Settings needs a moment to lay out and paint; without this the user sees an
// empty glass frame and then the controls popping in. Window opacity can't
// cover that gap because Avalonia applies it while drawing content, so the
// window is cloaked instead: Windows' compositor keeps it invisible while it
// renders normally underneath.
internal static class ZetlWindowReveal
{
    private const int DwmwaCloak = 13;
    // Uncloak by this point no matter what, so a missed frame callback can
    // never leave an invisible window holding the user's focus.
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(1500);

    public static void WhenReady(Window window, Action<TimeSpan>? onRevealed = null)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } handle)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        if (!SetCloaked(handle, true))
        {
            return;
        }

        var revealed = false;
        void Uncloak()
        {
            if (revealed)
            {
                return;
            }

            revealed = true;
            SetCloaked(handle, false);
            onRevealed?.Invoke(Stopwatch.GetElapsedTime(started));
        }

        void Reveal(object? sender, EventArgs e)
        {
            window.Opened -= Reveal;
            // Wait out two frames: the first is the one being laid out and
            // painted, the second request only runs once that frame has gone
            // to the compositor, so the window appears complete.
            window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ => Uncloak()));
        }

        window.Opened += Reveal;
        DispatcherTimer.RunOnce(Uncloak, Deadline);
    }

    private static bool SetCloaked(IntPtr handle, bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        return DwmSetWindowAttribute(handle, DwmwaCloak, ref value, sizeof(int)) == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
