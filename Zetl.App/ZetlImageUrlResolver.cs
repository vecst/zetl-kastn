using System.Net;
using SkiaSharp;

namespace ZETL;

/// <summary>
/// Downloads copied HTTP(S) image URLs with strict time and size bounds, then
/// decodes and re-encodes them as PNG before they enter project storage.
///
/// The request presents browser-like headers because image hosts behind
/// Cloudflare and forum software (XenForo attachment endpoints, CDNs) often
/// answer a bot-shaped request with a challenge or HTML page instead of the
/// image a browser would receive. When the response does not clearly declare an
/// image content type — a null type, or <c>application/octet-stream</c> on an
/// extension-less URL such as <c>/attachments/foo.123/</c> — the leading bytes
/// are sniffed so the image still resolves. Every rejection is logged so a
/// "why didn't this download" question is one diagnostics line, not an
/// investigation.
/// </summary>
internal sealed class ZetlImageUrlResolver : IImageUrlResolver
{
    internal const int MaximumDownloadBytes = 25 * 1024 * 1024;
    internal const long MaximumPixelCount = 40_000_000;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient SharedClient = CreateClient();

    private readonly HttpClient client;
    private readonly Action<string>? log;

    public ZetlImageUrlResolver(Action<string>? log = null)
        : this(SharedClient, log)
    {
    }

    internal ZetlImageUrlResolver(HttpClient client, Action<string>? log = null)
    {
        this.client = client;
        this.log = log;
    }

    public async Task<ZetlResolvedImageUrl?> TryResolveAsync(string text)
    {
        if (!TryParseHttpUrl(text, out var uri))
        {
            return null;
        }

        using var timeout = new CancellationTokenSource(DownloadTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(
                "image/avif,image/webp,image/png,image/*;q=0.8,*/*;q=0.5");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                log?.Invoke(
                    $"Image URL capture skipped: HTTP {(int)response.StatusCode} from {uri.Host}.");
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
            {
                log?.Invoke(
                    $"Image URL capture skipped: {response.Content.Headers.ContentLength} bytes exceeds the {MaximumDownloadBytes}-byte cap from {uri.Host}.");
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            var classification = ClassifyMediaType(mediaType);
            if (classification == MediaClassification.NotImage)
            {
                log?.Invoke(
                    $"Image URL capture skipped: content type '{mediaType}' is not an image from {finalUri.Host}.");
                return null;
            }

            var encodedBytes = await ReadBoundedAsync(response.Content, timeout.Token);
            if (encodedBytes is null)
            {
                log?.Invoke(
                    $"Image URL capture skipped: empty or oversized response body from {finalUri.Host}.");
                return null;
            }

            // When the server did not clearly declare an image type — a missing
            // type, or octet-stream on an extension-less URL — fall back to the
            // file's own signature so the image still resolves rather than
            // depending on the header alone.
            if (classification == MediaClassification.Unknown && !HasImageMagic(encodedBytes))
            {
                log?.Invoke(
                    $"Image URL capture skipped: '{mediaType ?? "no content type"}' body is not a recognized image format from {finalUri.Host}.");
                return null;
            }

            using var encodedData = SKData.CreateCopy(encodedBytes);
            using var codec = SKCodec.Create(encodedData);
            if (codec is null
                || codec.Info.Width <= 0
                || codec.Info.Height <= 0
                || (long)codec.Info.Width * codec.Info.Height > MaximumPixelCount)
            {
                log?.Invoke(
                    $"Image URL capture skipped: undecodable or oversized image from {finalUri.Host}.");
                return null;
            }

            using var bitmap = SKBitmap.Decode(encodedBytes);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                log?.Invoke(
                    $"Image URL capture skipped: image failed to decode from {finalUri.Host}.");
                return null;
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            if (png is null)
            {
                return null;
            }

            return new ZetlResolvedImageUrl(
                new ZetlClipboardImage(png.ToArray(), bitmap.Width, bitmap.Height),
                finalUri.AbsoluteUri);
        }
        catch (Exception ex) when (
            ex is HttpRequestException
                or IOException
                or InvalidOperationException
                or TaskCanceledException)
        {
            log?.Invoke($"Image URL capture failed ({ex.GetType().Name}).");
            return null;
        }
    }

    private static bool TryParseHttpUrl(string text, out Uri uri)
    {
        uri = null!;
        var trimmed = text.Trim();
        if (trimmed.IndexOfAny(['\r', '\n']) >= 0
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https"))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private enum MediaClassification
    {
        // The server clearly declared an image type; trust it and decode.
        Image,

        // No content type, or a generic binary type. The header tells us nothing,
        // so the body must be sniffed before it is trusted as an image.
        Unknown,

        // The server declared a concrete non-image type (text/html, json, ...).
        // Reject without downloading the whole body.
        NotImage,
    }

    private static MediaClassification ClassifyMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return MediaClassification.Unknown;
        }

        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return MediaClassification.Image;
        }

        return string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "binary/octet-stream", StringComparison.OrdinalIgnoreCase)
                ? MediaClassification.Unknown
                : MediaClassification.NotImage;
    }

    // Recognizes the container signatures SkiaSharp can decode. A match only
    // means "worth attempting to decode"; the SKCodec pass remains the final
    // authority on whether the bytes are a usable image.
    private static bool HasImageMagic(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12)
        {
            return false;
        }

        // PNG
        if (bytes.StartsWith(PngSignature))
        {
            return true;
        }

        // JPEG
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return true;
        }

        // GIF
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
        {
            return true;
        }

        // BMP
        if (bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            return true;
        }

        // WEBP: RIFF????WEBP
        if (bytes.StartsWith("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return true;
        }

        // ISO base media (AVIF/HEIC/HEIF): ????ftyp + an image brand
        if (bytes.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = bytes.Slice(8, 4);
            foreach (var imageBrand in IsoImageBrands)
            {
                if (brand.SequenceEqual(imageBrand))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly byte[][] IsoImageBrands =
    [
        "avif"u8.ToArray(), "avis"u8.ToArray(),
        "heic"u8.ToArray(), "heix"u8.ToArray(),
        "heim"u8.ToArray(), "heis"u8.ToArray(),
        "hevc"u8.ToArray(), "hevx"u8.ToArray(),
        "mif1"u8.ToArray(), "msf1"u8.ToArray(),
    ];

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumDownloadBytes)
            {
                return null;
            }

            output.Write(buffer, 0, read);
        }

        return output.Length == 0 ? null : output.ToArray();
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        var client = new HttpClient(handler);
        // Image hosts behind Cloudflare / forum software frequently answer a
        // bot-shaped request with a challenge or HTML page instead of the image
        // a browser would get. Presenting browser-like headers keeps an
        // explicitly-copied image URL resolving the way the user just saw it.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }
}
