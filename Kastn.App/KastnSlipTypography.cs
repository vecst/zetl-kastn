using Avalonia.Media;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

// A slip's authored typography as Avalonia values; null means "inherit the
// theme". Shared by the View blocks, Board cards, and the slip editor.
internal static class KastnSlipTypography
{
    public static FontFamily? FamilyOf(ZetlSlipSnapshot slip)
    {
        var family = ZetlViewRenderer.SlipFontFamily(slip);
        if (family.Length == 0)
        {
            return null;
        }

        try
        {
            return ZetlFontFamilies.Resolve(family);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static double? SizeOf(ZetlSlipSnapshot slip)
    {
        var size = ZetlViewRenderer.SlipFontSize(slip);
        return size > 0 ? size : null;
    }

    public static IBrush? ForegroundOf(ZetlSlipSnapshot slip) =>
        ZetlSlipTypography.TextColorRgb(slip.TextColor) is { } rgb
            ? new SolidColorBrush(Color.FromRgb(rgb.Red, rgb.Green, rgb.Blue))
            : null;
}
