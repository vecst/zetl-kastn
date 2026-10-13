using Avalonia.Controls;

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
        }
    }
}
