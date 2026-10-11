using ZETL;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlPlacementMathTests
{
    private const double AreaWidth = 1600;
    private const double AreaHeight = 900;
    private const double Width = 400;
    private const double Height = 300;
    private const double M = ZetlPlacementMath.Margin;

    private static (double X, double Y) Place(string anchor, (double X, double Y)? pointer = null) =>
        ZetlPlacementMath.Place(anchor, AreaWidth, AreaHeight, Width, Height, pointer);

    [Fact] public void TopCenterKeepsTheOriginalSixthOfTheWayDown()
    {
        AssertEqual((600.0, 150.0), Place(ZetlScreenAnchor.TopCenter), "Centered, a sixth of the way down.");

        var tall = ZetlPlacementMath.Place(ZetlScreenAnchor.TopCenter, AreaWidth, AreaHeight, Width, 820);
        AssertEqual(AreaHeight - 820 - M, tall.Y, "A tall window rises so its bottom stays on screen.");
    }

    [Fact] public void EachAnchorPlacesTheWindowInsideTheMargins()
    {
        AssertEqual((600.0, 300.0), Place(ZetlScreenAnchor.Center), "Center.");
        AssertEqual((AreaWidth - Width - M, M), Place(ZetlScreenAnchor.TopRight), "Top right.");
        AssertEqual((M, M), Place(ZetlScreenAnchor.TopLeft), "Top left.");
        AssertEqual((AreaWidth - Width - M, AreaHeight - Height - M), Place(ZetlScreenAnchor.BottomRight), "Bottom right.");
        AssertEqual((M, AreaHeight - Height - M), Place(ZetlScreenAnchor.BottomLeft), "Bottom left.");
    }

    [Fact] public void PointerPlacesBesideItAndFlipsAtTheEdges()
    {
        AssertEqual((100 + M, 100 + M), Place(ZetlScreenAnchor.Pointer, (100, 100)), "Below and right of the pointer.");
        AssertEqual(
            (1500 - M - Width, 800 - M - Height),
            Place(ZetlScreenAnchor.Pointer, (1500, 800)),
            "Above and left when the pointer is near the bottom-right corner.");
        AssertEqual(Place(ZetlScreenAnchor.TopCenter), Place(ZetlScreenAnchor.Pointer), "No pointer falls back to top center.");
    }

    [Fact] public void UnknownAnchorsAndOversizedWindowsStayOnScreen()
    {
        AssertEqual(ZetlScreenAnchor.TopCenter, ZetlScreenAnchor.Normalize("Somewhere"), "An unknown setting means top center.");
        AssertEqual(ZetlScreenAnchor.TopRight, ZetlScreenAnchor.Normalize(" topright "), "Settings match regardless of case.");

        var wide = ZetlPlacementMath.Place(ZetlScreenAnchor.TopRight, AreaWidth, AreaHeight, AreaWidth - 4, Height);
        AssertEqual(2.0, wide.X, "A window wider than the margins allow is centered rather than pushed off screen.");
    }

    [Fact] public void OpacityIsKeptWithinItsRange()
    {
        AssertEqual(60, ZetlPopupOpacity.Clamp(10), "Popups never become nearly invisible.");
        AssertEqual(100, ZetlPopupOpacity.Clamp(140), "Fully opaque is the maximum.");
        AssertEqual(85, ZetlPopupOpacity.Clamp(85), "Values in range are kept.");
    }

    // The Ubuntu desk: a 6K primary at 200% beside a 1080p screen Xwayland
    // shows doubled, with X11's one work area spanning both.
    [Fact] public void AScreensUsableAreaStopsAtItsOwnEdges()
    {
        var primary = (0, 0, 6144, 3456);
        var side = (6144, 0, 3840, 2160);
        var desktopWorkArea = (134, 64, 9850, 3392);

        AssertEqual((134, 64, 6010, 3392), ZetlPlacementMath.UsableArea(primary, desktopWorkArea), "The primary's part of the desktop.");
        AssertEqual((6144, 64, 3840, 2096), ZetlPlacementMath.UsableArea(side, desktopWorkArea), "The side screen's part.");
        AssertEqual(side, ZetlPlacementMath.UsableArea(side, (0, 0, 100, 100)), "A work area elsewhere falls back to the whole screen.");
    }

    [Fact] public void AToastFitsInsideAScaledScreen()
    {
        var at = ZetlPlacementMath.PlaceInPixels(ZetlScreenAnchor.BottomRight, (134, 64, 6010, 3392), 2, 360, 84);

        AssertEqual((6144 - 720 - 32, 3456 - 168 - 32), at, "Bottom right, sized and spaced in physical pixels.");
    }
}
