using Avalonia.Controls;
using Avalonia.Threading;

namespace ZETL;

// COSMIC places every window it manages itself and ignores move requests (a
// test window of every kind, October 12, 2026: only override-redirect ones went
// where they were asked), so the toast landed mid-screen and the hold ring
// covered the tour. There, those two windows are made override-redirect: the
// window manager leaves them alone, and they never take the keyboard anyway.
// Every other desktop keeps them as ordinary windows, as tested.
internal static class ZetlOverlayWindows
{
    public static bool Wanted { get; } =
        OperatingSystem.IsLinux()
        && (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
            .Split(':')
            .Any(desktop => desktop.Equals("COSMIC", StringComparison.OrdinalIgnoreCase));

    // Call before the window is first shown: the window manager decides
    // whether to manage a window when it maps.
    public static void Apply(Window window)
    {
        if (Wanted && window.TryGetPlatformHandle()?.Handle is { } handle)
        {
            ZetlX11Activation.MakeOverlay(handle, log: null);
            // Topmost is a window manager request, which an overlay has no use
            // for: it maps above every managed window as it is.
            window.Topmost = false;
        }
    }

    // COSMIC draws these overlays at 0,0 when they appear, though X11 has them
    // in place: it keeps the position it saw while the window was still an
    // ordinary one. It does follow a visible overlay's moves (checked with a
    // test window), so right after showing, the window is nudged a pixel and
    // back. Call just after Show.
    public static void SettleAfterShow(Window window)
    {
        if (!Wanted)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (window.IsVisible
                && ZetlWindowPlacement.LastPlaced(window) is { } position
                && window.TryGetPlatformHandle()?.Handle is { } handle)
            {
                ZetlX11Activation.MoveNow(handle, position.X + 1, position.Y);
                ZetlX11Activation.MoveNow(handle, position.X, position.Y);
            }
        }, DispatcherPriority.Background);
    }
}
