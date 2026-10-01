namespace ZETL;

// Where on the screen a Zetl popup (or the hold indicator) appears.
internal static class ZetlScreenAnchor
{
    public const string TopCenter = "TopCenter";
    public const string Center = "Center";
    public const string TopRight = "TopRight";
    public const string TopLeft = "TopLeft";
    public const string BottomRight = "BottomRight";
    public const string BottomLeft = "BottomLeft";
    // At the mouse pointer's last position, read once when the window opens.
    public const string Pointer = "Pointer";

    // In menu order, with the labels Settings shows.
    public static IReadOnlyList<(string Id, string Label)> Choices { get; } =
    [
        (TopCenter, "Top center"),
        (Center, "Center"),
        (TopRight, "Top right"),
        (TopLeft, "Top left"),
        (BottomRight, "Bottom right"),
        (BottomLeft, "Bottom left"),
        (Pointer, "At the mouse pointer")
    ];

    public static string Normalize(string? value) =>
        Choices.FirstOrDefault(choice =>
            string.Equals(choice.Id, value?.Trim(), StringComparison.OrdinalIgnoreCase)).Id
        ?? TopCenter;
}

// Popup appearance settings, kept in one place so popups and the hold
// indicator read them the same way.
internal static class ZetlPopupOpacity
{
    public const int Minimum = 60;
    public const int Maximum = 100;

    public static int Clamp(int percent) => Math.Clamp(percent, Minimum, Maximum);
}

// Pure placement math in logical units relative to a screen's working area, so
// every anchor can be tested without a display.
internal static class ZetlPlacementMath
{
    // Gap kept from screen edges, and between the pointer and a window placed
    // beside it.
    public const double Margin = 16;

    // The top-left corner for a window of the given size. The pointer is only
    // used by the Pointer anchor; without one, that anchor falls back to the
    // default top-center position.
    public static (double X, double Y) Place(
        string anchor,
        double areaWidth,
        double areaHeight,
        double width,
        double height,
        (double X, double Y)? pointer = null)
    {
        var right = areaWidth - width - Margin;
        var bottom = areaHeight - height - Margin;
        var centerX = (areaWidth - width) / 2;
        var (x, y) = ZetlScreenAnchor.Normalize(anchor) switch
        {
            ZetlScreenAnchor.Center => (centerX, (areaHeight - height) / 2),
            ZetlScreenAnchor.TopRight => (right, Margin),
            ZetlScreenAnchor.TopLeft => (Margin, Margin),
            ZetlScreenAnchor.BottomRight => (right, bottom),
            ZetlScreenAnchor.BottomLeft => (Margin, bottom),
            ZetlScreenAnchor.Pointer when pointer is { } at => BesidePointer(at, width, height, areaWidth, areaHeight),
            // Top center: a sixth of the way down, raised if the window would
            // otherwise run off the bottom.
            _ => (centerX, Math.Min(areaHeight / 6.0, bottom))
        };

        return (Fit(x, width, areaWidth), Fit(y, height, areaHeight));
    }

    // Below and to the right of the pointer, flipping to the other side when
    // that would run off the screen.
    private static (double X, double Y) BesidePointer(
        (double X, double Y) pointer,
        double width,
        double height,
        double areaWidth,
        double areaHeight)
    {
        var x = pointer.X + Margin + width <= areaWidth ? pointer.X + Margin : pointer.X - Margin - width;
        var y = pointer.Y + Margin + height <= areaHeight ? pointer.Y + Margin : pointer.Y - Margin - height;
        return (x, y);
    }

    // Keep the window inside the area with a margin; a window wider than the
    // area starts at its edge.
    private static double Fit(double position, double size, double areaSize)
    {
        var max = areaSize - size - Margin;
        return max < Margin ? Math.Max(0, (areaSize - size) / 2) : Math.Clamp(position, Margin, max);
    }
}
