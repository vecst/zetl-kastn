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
}
