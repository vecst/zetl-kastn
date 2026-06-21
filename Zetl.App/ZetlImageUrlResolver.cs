using System.Diagnostics;
using System.Net;
using SkiaSharp;

namespace ZETL;

/// <summary>
/// Downloads copied HTTP(S) image URLs with strict time and size bounds, then
/// decodes and re-encodes them as PNG before they enter project storage.
///
/// The in-process request presents browser-like headers because image hosts
/// behind Cloudflare and forum software (XenForo attachment endpoints, CDNs)
/// often answer a bot-shaped request with a challenge instead of the image a
/// browser would receive. When the response does not clearly declare an image
/// type — a null type, or <c>application/octet-stream</c> on an extension-less
/// URL such as <c>/attachments/foo.123/</c> — the leading bytes are sniffed so
/// the image still resolves.
///
/// Some hosts block on the TLS handshake fingerprint itself and return 403 to
/// .NET's <see cref="HttpClient"/> no matter the headers, while accepting the
/// system <c>curl</c> (which negotiates a different, allowed handshake). For
/// those, the resolver falls back to fetching through <c>curl</c>. Every
/// rejection is logged so a "why didn't this download" question is one
/// diagnostics line, not an investigation.
/// </summary>
internal sealed class ZetlImageUrlResolver : IImageUrlResolver
{
    internal const int MaximumDownloadBytes = 25 * 1024 * 1024;
    internal const long MaximumPixelCount = 40_000_000;
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string AcceptHeader =
        "image/avif,image/webp,image/png,image/*;q=0.8,*/*;q=0.5";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient SharedClient = CreateClient();

    private readonly HttpClient client;
    private readonly Action<string>? log;
    private readonly Func<Uri, CancellationToken, Task<(byte[] Bytes, string FinalUrl)?>> curlDownloader;

    public ZetlImageUrlResolver(Action<string>? log = null)
        : this(SharedClient, log)
    {
    }

    internal ZetlImageUrlResolver(
        HttpClient client,
        Action<string>? log = null,
        Func<Uri, CancellationToken, Task<(byte[] Bytes, string FinalUrl)?>>? curlDownloader = null)
    {
        this.client = client;
        this.log = log;
        this.curlDownloader = curlDownloader ?? CurlDownloadAsync;
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
            var downloaded = await DownloadAsync(uri, timeout.Token);
            return downloaded is { } image
                ? NormalizeToPng(image.Bytes, image.FinalUrl)
                : null;
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

    // Fetch encoded image bytes: the fast in-process request first, then the
    // system curl as a fallback when the host blocked .NET's TLS fingerprint
    // (a 403/401/429/451/503 challenge). curl is gated on those statuses so an
    // ordinary copied link (a 200 web page, a 404) never spawns a process.
    private async Task<(byte[] Bytes, string FinalUrl)?> DownloadAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        var (bytes, finalUrl, blocked) = await HttpDownloadAsync(uri, cancellationToken);
        if (bytes is not null)
        {
            return (bytes, finalUrl!);
        }

        if (blocked)
        {
            var viaCurl = await TryCurlAsync(uri, cancellationToken);
            if (viaCurl is { } result)
            {
                return result;
            }
        }

        return null;
    }

    // Returns the encoded bytes on success. Blocked is true only for the
    // bot-challenge statuses that warrant a curl retry.
    private async Task<(byte[]? Bytes, string? FinalUrl, bool Blocked)> HttpDownloadAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd(AcceptHeader);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var blocked = IsLikelyBlock(response.StatusCode);
            log?.Invoke(
                $"Image URL in-process fetch got HTTP {(int)response.StatusCode} from {uri.Host}"
                + (blocked ? "; retrying with system curl." : "."));
            return (null, null, blocked);
        }

        if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
        {
            log?.Invoke(
                $"Image URL capture skipped: {response.Content.Headers.ContentLength} bytes exceeds the {MaximumDownloadBytes}-byte cap from {uri.Host}.");
            return (null, null, false);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        var classification = ClassifyMediaType(mediaType);
        if (classification == MediaClassification.NotImage)
        {
            log?.Invoke(
                $"Image URL capture skipped: content type '{mediaType}' is not an image from {finalUri.Host}.");
            return (null, null, false);
        }

        var encodedBytes = await ReadBoundedAsync(response.Content, cancellationToken);
        if (encodedBytes is null)
        {
            log?.Invoke(
                $"Image URL capture skipped: empty or oversized response body from {finalUri.Host}.");
            return (null, null, false);
        }

        // When the server did not clearly declare an image type — a missing type,
        // or octet-stream on an extension-less URL — fall back to the file's own
        // signature so the image still resolves rather than depending on the
        // header alone.
        if (classification == MediaClassification.Unknown && !HasImageMagic(encodedBytes))
        {
            log?.Invoke(
                $"Image URL capture skipped: '{mediaType ?? "no content type"}' body is not a recognized image format from {finalUri.Host}.");
            return (null, null, false);
        }

        return (encodedBytes, finalUri.AbsoluteUri, false);
    }

    private static bool IsLikelyBlock(HttpStatusCode status) => status is
        HttpStatusCode.Forbidden
        or HttpStatusCode.Unauthorized
        or HttpStatusCode.TooManyRequests
        or HttpStatusCode.UnavailableForLegalReasons
        or HttpStatusCode.ServiceUnavailable;

    private async Task<(byte[] Bytes, string FinalUrl)?> TryCurlAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await curlDownloader(uri, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                log?.Invoke($"Image URL curl fallback did not return an image from {uri.Host}.");
            }

            return result;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Image URL curl fallback failed ({ex.GetType().Name}): {ex.Message}");
            return null;
        }
    }

    // Decode and re-encode to PNG. The SKCodec pass is the final authority on
    // whether the bytes are a usable image, so curl bytes (which carry no content
    // type) are validated here just like in-process bytes.
    private ZetlResolvedImageUrl? NormalizeToPng(byte[] encodedBytes, string finalUrl)
    {
        using var encodedData = SKData.CreateCopy(encodedBytes);
        using var codec = SKCodec.Create(encodedData);
        if (codec is null
            || codec.Info.Width <= 0
            || codec.Info.Height <= 0
            || (long)codec.Info.Width * codec.Info.Height > MaximumPixelCount)
        {
            log?.Invoke($"Image URL capture skipped: undecodable or oversized image from {finalUrl}.");
            return null;
        }

        using var bitmap = SKBitmap.Decode(encodedBytes);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            log?.Invoke($"Image URL capture skipped: image failed to decode from {finalUrl}.");
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
            finalUrl);
    }

    // Fetch through the system curl, whose TLS handshake some hosts accept where
    // HttpClient's is rejected. The URL is passed as an argument (never a shell
    // string) and redirects are constrained to http/https, matching the
    // in-process protocol guard. The effective URL after redirects is written to
    // stderr via -w so it does not corrupt the binary body on stdout.
    private static async Task<(byte[] Bytes, string FinalUrl)?> CurlDownloadAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveCurlPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string[] arguments =
        [
            "-s", "-L",
            "--max-time", "10",
            "--max-filesize", MaximumDownloadBytes.ToString(),
            "--proto", "=http,https",
            "--proto-redir", "=http,https",
            "-A", BrowserUserAgent,
            "-H", "Accept: " + AcceptHeader,
            "-w", "%{stderr}%{url_effective}",
            "-o", "-",
            uri.AbsoluteUri,
        ];
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        try
        {
            var stdoutTask = ReadBoundedStreamAsync(
                process.StandardOutput.BaseStream, cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var bytes = await stdoutTask.ConfigureAwait(false);
            var effectiveUrl = (await stderrTask.ConfigureAwait(false)).Trim();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0 || bytes is null || bytes.Length == 0)
            {
                return null;
            }

            return (bytes, effectiveUrl.Length > 0 ? effectiveUrl : uri.AbsoluteUri);
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort cleanup; the process is exiting on its own bounds.
                }
            }
        }
    }

    private static string ResolveCurlPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var system32Curl = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "curl.exe");
            if (File.Exists(system32Curl))
            {
                return system32Curl;
            }
        }

        return "curl";
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
        return await ReadBoundedStreamAsync(input, cancellationToken);
    }

    private static async Task<byte[]?> ReadBoundedStreamAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
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
        // Browser-like headers satisfy hosts that gate only on the User-Agent.
        // Hosts that gate on the TLS fingerprint still reject HttpClient; those
        // are handled by the curl fallback.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }
}
