using Avalonia.Controls;
using ZETL;

namespace KASTN;

/// <summary>
/// Builds Kastn's window/taskbar icon procedurally, mirroring the way Zetl draws
/// its tray icon (a white glyph over a left-to-right gradient). Kastn uses a "K"
/// over a dusk aubergine→periwinkle fade so it reads as a sibling of Zetl's "Z"
/// without shipping a binary asset.
/// </summary>
internal static class KastnIcon
{
    private const uint LeftColor = 0x5B4B6E;   // aubergine
    private const uint RightColor = 0x8E7BC4;  // periwinkle

    private static WindowIcon? cached;

    public static WindowIcon Create() => cached ??= Build();

    private static WindowIcon Build() =>
        ZetlGlyphIcon.Create(size: 32, LeftColor, RightColor, IsGlyph);

    // A blocky "K": a vertical bar on the left plus two diagonal arms meeting it
    // near the middle.
    private static bool IsGlyph(int x, int y)
    {
        if (x is >= 7 and <= 10 && y is >= 5 and <= 26)
        {
            return true;
        }

        if (x is >= 11 and <= 24 && y is >= 4 and <= 26)
        {
            var upper = 15 - (x - 11);
            var lower = 16 + (x - 11);
            return Math.Abs(y - upper) <= 1 || Math.Abs(y - lower) <= 1;
        }

        return false;
    }
}
