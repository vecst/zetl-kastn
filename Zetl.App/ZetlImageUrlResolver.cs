using System.Net;
using System.Net.Http.Headers;
using SkiaSharp;

namespace ZETL;

/// <summary>
/// Downloads copied HTTP(S) image URLs with strict time and size bounds, then
/// decodes and re-encodes them as PNG before they enter project storage.
/// </summary>
internal sealed class ZetlImageUrlResolver : IImageUrlResolver
{
    internal const int MaximumDownloadBytes = 25 * 1024 * 1024;
    internal const long MaximumPixelCount = 40_000_000;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient SharedClient = CreateClient();
    private static readonly HashSet<string> ImageExtensions = new(
        [".avif", ".bmp", ".gif", ".heic", ".heif", ".jpeg", ".jpg", ".png", ".webp"],
        StringComparer.OrdinalIgnoreCase);

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
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength is > MaximumDownloadBytes)
            {
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            if (!LooksLikeImageContent(mediaType, finalUri))
            {
                return null;
            }

            var encodedBytes = await ReadBoundedAsync(response.Content, timeout.Token);
            if (encodedBytes is null)
            {
                return null;
            }

            using var encodedData = SKData.CreateCopy(encodedBytes);
            using var codec = SKCodec.Create(encodedData);
            if (codec is null
                || codec.Info.Width <= 0
                || codec.Info.Height <= 0
                || (long)codec.Info.Width * codec.Info.Height > MaximumPixelCount)
            {
                return null;
            }

            using var bitmap = SKBitmap.Decode(encodedBytes);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
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

    private static bool LooksLikeImageContent(string? mediaType, Uri uri)
    {
        if (mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (mediaType is not null
            && !string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ImageExtensions.Contains(Path.GetExtension(uri.AbsolutePath));
    }

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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Zetl/1.0");
        return client;
    }
}
