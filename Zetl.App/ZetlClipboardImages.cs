using System.Buffers.Binary;
using Avalonia.Media.Imaging;

namespace ZETL;

/// <summary>
/// Clipboard image normalization shared by the platform clipboards: every
/// picture Zetl keeps is a validated PNG with known dimensions.
/// </summary>
internal static class ZetlClipboardImages
{
    internal static bool TryCreatePngSnapshot(
        byte[] pngBytes,
        out ZetlClipboardImage? image)
    {
        ReadOnlySpan<byte> png = pngBytes;
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        ReadOnlySpan<byte> ihdr = "IHDR"u8;
        ReadOnlySpan<byte> iend = "IEND"u8;
        if (png.Length < 33
            || !png[..8].SequenceEqual(signature)
            || BinaryPrimitives.ReadUInt32BigEndian(png.Slice(8, 4)) != 13
            || !png.Slice(12, 4).SequenceEqual(ihdr))
        {
            image = null;
            return false;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            image = null;
            return false;
        }

        var offset = 8;
        var foundEnd = false;
        while (offset <= png.Length - 12)
        {
            var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(
                png.Slice(offset, 4));
            var chunkEnd = (ulong)offset + 12UL + chunkLength;
            if (chunkEnd > (ulong)png.Length)
            {
                image = null;
                return false;
            }

            var chunkType = png.Slice(offset + 4, 4);
            if (chunkType.SequenceEqual(iend))
            {
                foundEnd = chunkLength == 0 && chunkEnd == (ulong)png.Length;
                break;
            }

            offset = (int)chunkEnd;
        }

        if (!foundEnd)
        {
            image = null;
            return false;
        }

        image = new ZetlClipboardImage(pngBytes, (int)width, (int)height);
        return true;
    }

    internal static bool TryNormalizeImage(byte[] source, out ZetlClipboardImage? image)
    {
        try
        {
            using var input = new MemoryStream(source, writable: false);
            using var bitmap = new Bitmap(input);
            using var output = new MemoryStream();
            bitmap.Save(output);
            image = new ZetlClipboardImage(
                output.ToArray(),
                bitmap.PixelSize.Width,
                bitmap.PixelSize.Height);
            return image.PngBytes.Length > 0 && image.Width > 0 && image.Height > 0;
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
        {
            image = null;
            return false;
        }
    }

    /// <summary>
    /// A clipboard payload of the given MIME type as a normalized PNG: PNG bytes
    /// are validated and kept as they are, anything else is decoded and re-encoded.
    /// </summary>
    public static ZetlClipboardImage? Normalize(byte[] bytes, string mimeType) =>
        mimeType == "image/png" && TryCreatePngSnapshot(bytes, out var png)
            ? png
            : TryNormalizeImage(bytes, out var image) ? image : null;
}
