namespace ZETL;

/// <summary>
/// Reads a copied <c>data:image/...;base64,...</c> URI, the text a browser's
/// "Copy image address" gives for an inline image. Only the envelope is checked
/// here: an image media type, base64 encoding, and the same size cap as an
/// image download. Decoding the bytes as an image (and rejecting what is not
/// one) stays with the image normalizer, so a malformed or unsupported URI
/// simply stays text.
/// </summary>
internal static class ZetlDataImageUri
{
    public static bool TryDecode(string text, int maximumBytes, out byte[] bytes, out string mediaType)
    {
        bytes = [];
        mediaType = "";
        var trimmed = text.AsSpan().Trim();
        if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var comma = trimmed.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }

        // data:[<media type>][;parameter=value]*;base64,<payload>
        var header = trimmed[5..comma];
        var parts = header.ToString().Split(';', StringSplitOptions.TrimEntries);
        if (parts.Length < 2
            || !parts[0].StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parts[^1], "base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var payload = trimmed[(comma + 1)..];
        // Reject before decoding: base64 carries 3 bytes per 4 characters.
        if (payload.IsEmpty || (long)payload.Length / 4 * 3 > maximumBytes + 3)
        {
            return false;
        }

        var buffer = new byte[payload.Length / 4 * 3 + 3];
        // Line breaks and spaces are allowed inside a pasted base64 payload.
        if (!Convert.TryFromBase64String(payload.ToString(), buffer, out var written)
            || written == 0
            || written > maximumBytes)
        {
            return false;
        }

        bytes = buffer[..written];
        mediaType = parts[0].ToLowerInvariant();
        return true;
    }
}
