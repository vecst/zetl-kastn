using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace ZETL;

// Keeps Zetl windows on-screen. Window dimensions are authored for a desktop
// monitor, so on a small laptop display they must be shrunk to fit and pulled
// up from the top edge rather than opening partly off-screen or behind the
// taskbar. Larger windows can also request an extra "compact" reduction that
// only applies when they cannot fit at their authored size, so they do not fill
// the whole screen.
internal static class ZetlWindowPlacement
{
    private const double Margin = 16;

    private sealed class Placement
    {
        public double CompactWidth;
        public double CompactHeight;
        public double AuthoredWidth = double.NaN;
        public double AuthoredHeight = double.NaN;
        // Hold popups follow the popup position and opacity settings; other
        // windows keep the top-center default.
        public bool IsPopup;
        // Read once when the popup is shown: where the pointer was and the window
        // the user was working in, which picks the screen.
        public PixelPoint? Pointer;
        public PixelPoint? WorkingWindowCenter;
        // Where Zetl last put the window, held briefly against the window
        // manager (see MoveTo).
        public PixelPoint? Intended;
        public long HoldUntil;
        public bool Watching;
    }

    // How long after placing a window a window manager's move is undone, and
    // the smallest move that counts: title-bar offsets some desktops report
    // are well under it, a jump to another monitor far over it.
    private static readonly TimeSpan HoldPlacementFor = TimeSpan.FromMilliseconds(750);
    private const int HoldPlacementBeyond = 64;

    private static readonly ConditionalWeakTable<Window, Placement> Placements = new();

    // Set from settings by the host.
    public static string PopupPosition { get; set; } = ZetlScreenAnchor.TopCenter;
    public static int PopupOpacityPercent { get; set; } = ZetlPopupOpacity.Maximum;

    // Fit a window to the screen every time it opens. Done on Opened (not before
    // Show) because a window's screen is only reliably known once it has been
    // realized; doing it earlier silently no-ops and the window falls back to an
    // OS-chosen position. compactWidth/compactHeight trim the window further on
    // displays too small for its authored size.
    public static void Track(
        Window window,
        double compactWidth = 0,
        double compactHeight = 0,
        bool isPopup = false)
    {
        var placement = Placements.GetValue(window, _ => new Placement());
        placement.CompactWidth = compactWidth;
        placement.CompactHeight = compactHeight;
        placement.IsPopup = isPopup;
        window.Opened += (_, _) => FitToScreen(window);
    }

    // Record where the pointer is and which window the user is working in, just
    // before a popup is shown. One read each, no tracking in between.
    public static void CaptureContext(Window window, IntPtr? workingWindow)
    {
        var placement = Placements.GetValue(window, _ => new Placement());
        placement.Pointer = null;
        placement.WorkingWindowCenter = null;
        if (!OperatingSystem.IsWindows())
        {
            // X11's pointer stays where it last left an X11 window while the
            // mouse is over a Wayland one; Hyprland says where it really is.
            if (OperatingSystem.IsLinux() && HyprlandIpc.TryGetCursor() is { } cursor)
            {
                placement.Pointer = new PixelPoint(cursor.X, cursor.Y);
            }

            return;
        }

        if (Win32Interop.GetCursorPos(out var pointer))
        {
            placement.Pointer = new PixelPoint(pointer.X, pointer.Y);
        }

        if (workingWindow is { } handle
            && handle != IntPtr.Zero
            && Win32Interop.GetWindowRect(handle, out var bounds))
        {
            placement.WorkingWindowCenter = new PixelPoint(
                bounds.Left + (bounds.Right - bounds.Left) / 2,
                bounds.Top + (bounds.Bottom - bounds.Top) / 2);
        }
    }

    // Clamp a window to the screen working area and center it horizontally,
    // flowing from near the top with the bottom (and its action buttons) kept
    // on-screen. The working area is in physical pixels while Width/Height are
    // logical, so the comparison goes through the screen scaling — without that
    // the fit check fails on the 125-150% displays most laptops use. A window's
    // own minimum is relaxed when it alone is larger than the screen, so the
    // content (which scrolls) can still fit.
    public static void FitToScreen(Window window)
    {
        var placement = Placements.GetValue(window, _ => new Placement());
        var anchor = placement.IsPopup ? ZetlScreenAnchor.Normalize(PopupPosition) : ZetlScreenAnchor.TopCenter;
        var screen = ChooseScreen(window, placement, anchor);
        if (screen is null)
        {
            return;
        }

        // Capture the authored size once, before the first shrink, so a later
        // fit can restore it when the display is large enough.
        if (double.IsNaN(placement.AuthoredWidth) && !double.IsNaN(window.Width))
        {
            placement.AuthoredWidth = window.Width;
        }

        if (double.IsNaN(placement.AuthoredHeight) && !double.IsNaN(window.Height))
        {
            placement.AuthoredHeight = window.Height;
        }

        var authoredWidth = double.IsNaN(placement.AuthoredWidth) ? window.Width : placement.AuthoredWidth;
        var authoredHeight = double.IsNaN(placement.AuthoredHeight) ? window.Height : placement.AuthoredHeight;
        if (double.IsNaN(authoredWidth) || double.IsNaN(authoredHeight))
        {
            return;
        }

        var area = UsableArea(screen);
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var areaWidth = area.Width / scaling;
        var areaHeight = area.Height / scaling;

        var maxWidth = Math.Max(1, areaWidth - Margin);
        var maxHeight = Math.Max(1, areaHeight - Margin);

        // Use the authored size when it fits; otherwise shrink to the screen and
        // trim the per-window compact amount so the window leaves a margin.
        var fits = authoredWidth <= maxWidth && authoredHeight <= maxHeight;
        var targetWidth = Math.Clamp(fits ? authoredWidth : maxWidth - placement.CompactWidth, 1, maxWidth);
        var targetHeight = Math.Clamp(fits ? authoredHeight : maxHeight - placement.CompactHeight, 1, maxHeight);

        if (window.MinWidth > targetWidth)
        {
            window.MinWidth = targetWidth;
        }

        if (window.MinHeight > targetHeight)
        {
            window.MinHeight = targetHeight;
        }

        window.Width = targetWidth;
        window.Height = targetHeight;

        (double X, double Y)? pointer = placement.Pointer is { } at
            ? ((at.X - area.X) / scaling, (at.Y - area.Y) / scaling)
            : null;
        var (x, y) = ZetlPlacementMath.Place(anchor, areaWidth, areaHeight, targetWidth, targetHeight, pointer);

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        MoveTo(window, new PixelPoint(
            area.X + (int)(x * scaling),
            area.Y + (int)(y * scaling)));
        if (placement.IsPopup)
        {
            ApplyPopupOpacity(window, PopupOpacityPercent);
        }
    }

    public static void ApplyPopupOpacity(Window window, int percent)
    {
        var opacity = ZetlPopupOpacity.Clamp(percent) / 100.0;
        window.Opacity = opacity;
        // Zetl renders in software on Windows, and a software window is
        // presented opaque unless it asks for a transparent surface, so without
        // this hint a faded popup showed black behind it instead of the desktop.
        // A fully opaque popup skips the hint and keeps the plain surface.
        window.TransparencyLevelHint = opacity < 1
            ? [WindowTransparencyLevel.Transparent]
            : [];
    }

    // Place a small overlay (the hold indicator) at an anchor on the screen the
    // user is working on, reading the pointer and foreground window once.
    public static void PlaceOverlay(Window window, double width, double height, string anchor)
    {
        var placement = Placements.GetValue(window, _ => new Placement());
        placement.IsPopup = true;
        CaptureContext(
            window,
            OperatingSystem.IsWindows() ? Win32Interop.GetForegroundWindow() : null);
        var screen = ChooseScreen(window, placement, anchor);
        if (screen is null)
        {
            return;
        }

        var area = UsableArea(screen);
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        (double X, double Y)? pointer = placement.Pointer is { } at
            ? ((at.X - area.X) / scaling, (at.Y - area.Y) / scaling)
            : null;
        var (x, y) = ZetlPlacementMath.Place(
            anchor,
            area.Width / scaling,
            area.Height / scaling,
            width,
            height,
            pointer);
        MoveTo(window, new PixelPoint(
            area.X + (int)(x * scaling),
            area.Y + (int)(y * scaling)));
    }

    // Puts a window where Zetl placed it, and keeps it there as it appears.
    // Hyprland moves an X11 window that opens while the pointer is on another
    // monitor by that monitor's offset (a popup meant for the 4K landed
    // straddling both screens); a window manager's move right after placing
    // is undone once. A later move, like the user dragging it, is left alone.
    public static void MoveTo(Window window, PixelPoint position)
    {
        window.Position = position;
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var placement = Placements.GetValue(window, _ => new Placement());
        placement.Intended = position;
        placement.HoldUntil = Stopwatch.GetTimestamp() + (long)(HoldPlacementFor.TotalSeconds * Stopwatch.Frequency);
        if (!placement.Watching)
        {
            placement.Watching = true;
            window.PositionChanged += OnPositionChanged;
        }
    }

    private static void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (sender is not Window window
            || !Placements.TryGetValue(window, out var placement)
            || placement.Intended is not { } intended
            || Stopwatch.GetTimestamp() > placement.HoldUntil
            || (Math.Abs(e.Point.X - intended.X) <= HoldPlacementBeyond
                && Math.Abs(e.Point.Y - intended.Y) <= HoldPlacementBeyond))
        {
            return;
        }

        placement.Intended = null;
        Dispatcher.UIThread.Post(() => window.Position = intended);
    }

    // The part of a screen windows may use (see ZetlPlacementMath.UsableArea).
    public static PixelRect UsableArea(Screen screen)
    {
        var (x, y, width, height) = ZetlPlacementMath.UsableArea(Tuple(screen.Bounds), Tuple(screen.WorkingArea));
        return new PixelRect(x, y, width, height);
    }

    // A fixed-size window at an anchor of a screen, or of a stand-in area
    // when there is no screen to ask.
    public static PixelPoint PlaceOnScreen(Screen? screen, string anchor, double width, double height)
    {
        var (x, y) = ZetlPlacementMath.PlaceInPixels(
            anchor,
            screen is null ? (0, 0, 1200, 800) : Tuple(UsableArea(screen)),
            screen?.Scaling ?? 1,
            width,
            height);
        return new PixelPoint(x, y);
    }

    private static (int X, int Y, int Width, int Height) Tuple(PixelRect rect) =>
        (rect.X, rect.Y, rect.Width, rect.Height);

    // A popup at the pointer opens on the pointer's screen; any other popup on
    // the screen of the window the user was working in. Without either, and for
    // ordinary dialogs, the primary screen.
    private static Screen? ChooseScreen(Window window, Placement placement, string anchor)
    {
        var reference = placement.IsPopup
            ? anchor == ZetlScreenAnchor.Pointer
                ? placement.Pointer ?? placement.WorkingWindowCenter
                : placement.WorkingWindowCenter ?? placement.Pointer
            : null;
        return (reference is { } point ? window.Screens.ScreenFromPoint(point) : null)
            ?? window.Screens.Primary;
    }
}
