using Avalonia.Controls;

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

    private static WindowIcon Build()
    {
        const int size = 32;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // ICONDIR
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            // ICONDIRENTRY
            writer.Write((byte)size);
            writer.Write((byte)size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)(40 + (size * size * 4) + (size * 4)));
            writer.Write((uint)22);
            // BITMAPINFOHEADER
            writer.Write((uint)40);
            writer.Write(size);
            writer.Write(size * 2);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)0);
            writer.Write((uint)(size * size * 4));
            writer.Write(0);
            writer.Write(0);
            writer.Write((uint)0);
            writer.Write((uint)0);

            // BGRA pixels, bottom-up.
            for (var y = size - 1; y >= 0; y--)
            {
                for (var x = 0; x < size; x++)
                {
                    var color = IsGlyph(x, y)
                        ? 0xFFFFFFu
                        : GradientColor(LeftColor, RightColor, x, size);
                    writer.Write((byte)(color & 0xFF));
                    writer.Write((byte)((color >> 8) & 0xFF));
                    writer.Write((byte)((color >> 16) & 0xFF));
                    writer.Write((byte)255);
                }
            }

            // AND mask: fully opaque (all zero), rows padded to 32 bits.
            for (var index = 0; index < size * 4; index++)
            {
                writer.Write((byte)0);
            }
        }

        stream.Position = 0;
        return new WindowIcon(stream);
    }

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

    private static uint GradientColor(uint left, uint right, int x, int size)
    {
        var t = size <= 1 ? 0d : (double)x / (size - 1);
        var r = Lerp((left >> 16) & 0xFF, (right >> 16) & 0xFF, t);
        var g = Lerp((left >> 8) & 0xFF, (right >> 8) & 0xFF, t);
        var b = Lerp(left & 0xFF, right & 0xFF, t);
        return ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static int Lerp(uint from, uint to, double t) =>
        (int)Math.Round(from + ((double)to - from) * t);
}
