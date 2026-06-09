using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;

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
    }

    private static readonly ConditionalWeakTable<Window, Placement> Placements = new();

    // Fit a window to the screen every time it opens. Done on Opened (not before
    // Show) because a window's screen is only reliably known once it has been
    // realized; doing it earlier silently no-ops and the window falls back to an
    // OS-chosen position. compactWidth/compactHeight trim the window further on
    // displays too small for its authored size.
    public static void Track(Window window, double compactWidth = 0, double compactHeight = 0)
    {
        var placement = Placements.GetValue(window, _ => new Placement());
        placement.CompactWidth = compactWidth;
        placement.CompactHeight = compactHeight;
        window.Opened += (_, _) => FitToScreen(window);
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
        var screen = window.Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var placement = Placements.GetValue(window, _ => new Placement());
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

        var area = screen.WorkingArea;
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

        var x = Math.Max(0, (areaWidth - targetWidth) / 2);
        var y = Math.Max(Margin, Math.Min(areaHeight / 6.0, areaHeight - targetHeight - Margin));

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(
            area.X + (int)(x * scaling),
            area.Y + (int)(y * scaling));
    }
}
