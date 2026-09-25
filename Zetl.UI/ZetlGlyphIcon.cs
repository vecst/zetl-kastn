using Avalonia.Controls;

namespace ZETL;

// Builds the procedural app icons (Zetl's tray "Z", Kastn's window "K"): a white
// glyph over a left-to-right gradient, written as a single-image 32-bit ICO so
// no binary asset ships.
internal static class ZetlGlyphIcon
{
    public static WindowIcon Create(
        int size,
        uint leftColor,
        uint rightColor,
        Func<int, int, bool> isGlyph)
    {
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
            // BITMAPINFOHEADER; the height counts the XOR and AND masks together.
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
                    var color = isGlyph(x, y)
                        ? 0xFFFFFFu
                        : GradientColor(leftColor, rightColor, x, size);
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

    // Interpolates a 0xRRGGBB color across the icon width: x=0 is left,
    // x=size-1 is right. Equal endpoints yield a solid fill.
    private static uint GradientColor(uint left, uint right, int x, int size)
    {
        var t = size <= 1 ? 0d : x / (double)(size - 1);
        var r = Lerp((left >> 16) & 0xFF, (right >> 16) & 0xFF, t);
        var g = Lerp((left >> 8) & 0xFF, (right >> 8) & 0xFF, t);
        var b = Lerp(left & 0xFF, right & 0xFF, t);
        return (r << 16) | (g << 8) | b;
    }

    private static uint Lerp(uint from, uint to, double t) =>
        (uint)Math.Round(from + ((double)to - from) * t);
}
