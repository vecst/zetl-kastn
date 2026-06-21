namespace ZETL;

// Windows-only smoke tests for the real platform clipboard, run via
// `dotnet run --project Zetl.App -- --self-test`. They exercise the live
// Windows clipboard (write/read round-trips), which portable tests can't do, so
// they are deliberately out of the portable suite. The caller's clipboard is
// captured and restored.
internal static class ZetlWindowsSelfTests
{
    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP Zetl Windows self-tests: not running on Windows.");
            return 0;
        }

        var clipboard = new AvaloniaWindowsClipboard(message => Console.WriteLine($"  clipboard: {message}"));
        var original = clipboard.TryGetText();
        var failures = 0;
        try
        {
            var dib = new byte[44];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0, 4), 40);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4, 4), 1);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8, 4), 1);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12, 2), 1);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14, 2), 24);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20, 4), 4);
            dib[40] = 0x33;
            dib[41] = 0x66;
            dib[42] = 0x99;
            var bmp = AvaloniaWindowsClipboard.AddBitmapFileHeader(dib);
            failures += Check(
                "clipboard DIB conversion produces a valid BMP envelope",
                bmp is { Length: 58 }
                && bmp[0] == (byte)'B'
                && bmp[1] == (byte)'M'
                && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                    bmp.AsSpan(10, 4)) == 54);

            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            var outputDib = AvaloniaWindowsClipboard.CreateDib(png);
            failures += Check(
                "clipboard PNG output produces a 32-bit DIB",
                outputDib.Length == 44
                && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                    outputDib.AsSpan(4, 4)) == 1
                && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                    outputDib.AsSpan(8, 4)) == -1
                && System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(
                    outputDib.AsSpan(14, 2)) == 32);

            // The stub keys off the request path so one handler can exercise each
            // content-type classification branch in the resolver.
            //   /image       declared image/png            -> resolves
            //   /page        declared text/html            -> rejected (not image)
            //   /octet       octet-stream + real PNG bytes -> resolves via sniff
            //   /octet-html  octet-stream + HTML bytes     -> rejected by sniff
            //   /missing     no content type + PNG bytes   -> resolves via sniff
            //   /blocked     403                           -> triggers curl fallback
            using var resolverClient = new HttpClient(new StubHttpHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath;
                if (path is "/blocked")
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
                    {
                        RequestMessage = request,
                        Content = new ByteArrayContent("<html>blocked</html>"u8.ToArray()),
                    };
                }

                var servesImageBytes = path is "/image" or "/octet" or "/missing";
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(servesImageBytes
                        ? png
                        : "<html>not an image</html>"u8.ToArray())
                };
                var declaredType = path switch
                {
                    "/image" => "image/png",
                    "/page" => "text/html",
                    "/octet" or "/octet-html" => "application/octet-stream",
                    _ => null,
                };
                if (declaredType is not null)
                {
                    response.Content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue(declaredType);
                }

                return response;
            }));
            // A curl fallback that throws if it is ever asked: proves the normal
            // 200-image path resolves in-process without spawning a fallback.
            var resolver = new ZetlImageUrlResolver(
                resolverClient,
                curlDownloader: (_, _) =>
                    throw new InvalidOperationException("curl should not run for a 200 response."));
            var resolvedImage = resolver.TryResolveAsync("https://example.test/image")
                .GetAwaiter().GetResult();
            failures += Check(
                "image URL resolver downloads and normalizes image content",
                resolvedImage?.Image is { Width: 1, Height: 1 }
                && resolvedImage.Image.PngBytes.Length > 0
                && resolvedImage.SourceUrl == "https://example.test/image");
            failures += Check(
                "image URL resolver rejects HTML content",
                resolver.TryResolveAsync("https://example.test/page")
                    .GetAwaiter().GetResult() is null);
            failures += Check(
                "image URL resolver sniffs octet-stream image bytes",
                resolver.TryResolveAsync("https://example.test/octet")
                    .GetAwaiter().GetResult()?.Image is { Width: 1, Height: 1 });
            failures += Check(
                "image URL resolver rejects octet-stream non-image bytes",
                resolver.TryResolveAsync("https://example.test/octet-html")
                    .GetAwaiter().GetResult() is null);
            failures += Check(
                "image URL resolver sniffs image bytes when no content type is sent",
                resolver.TryResolveAsync("https://example.test/missing")
                    .GetAwaiter().GetResult()?.Image is { Width: 1, Height: 1 });

            // A blocked (403) response falls back to curl. A fallback that returns
            // image bytes resolves; one that returns nothing yields a text slip.
            var curlResolver = new ZetlImageUrlResolver(
                resolverClient,
                curlDownloader: (uri, _) =>
                    Task.FromResult<(byte[] Bytes, string FinalUrl)?>((png, uri.AbsoluteUri)));
            var viaCurl = curlResolver.TryResolveAsync("https://example.test/blocked")
                .GetAwaiter().GetResult();
            failures += Check(
                "image URL resolver falls back to curl on a blocked response",
                viaCurl?.Image is { Width: 1, Height: 1 }
                && viaCurl.SourceUrl == "https://example.test/blocked");

            var noCurlResolver = new ZetlImageUrlResolver(
                resolverClient,
                curlDownloader: (_, _) =>
                    Task.FromResult<(byte[] Bytes, string FinalUrl)?>(null));
            failures += Check(
                "image URL resolver gives up when the curl fallback returns nothing",
                noCurlResolver.TryResolveAsync("https://example.test/blocked")
                    .GetAwaiter().GetResult() is null);

            var sample = $"zetl-selftest-{Guid.NewGuid():N}";
            failures += Check("clipboard write reports success", clipboard.SetText(sample));
            failures += Check("clipboard round-trips written text", clipboard.TryGetText() == sample);

            var tokenBefore = clipboard.GetChangeToken();
            failures += Check("clipboard overwrite reports success", clipboard.SetText("second value"));
            failures += Check("clipboard reflects the overwrite", clipboard.TryGetText() == "second value");
            failures += Check("change token advances after a write", clipboard.GetChangeToken() != tokenBefore);

            failures += Check(
                "clipboard round-trips unicode and emoji",
                clipboard.SetText("café — naïve — 日本語 🎉") && clipboard.TryGetText() == "café — naïve — 日本語 🎉");

            // Simulate owner-window creation failing: the write must refuse and
            // leave whatever is on the clipboard intact, never empty it.
            var canary = $"zetl-owner-canary-{Guid.NewGuid():N}";
            clipboard.SetText(canary);
            var noOwner = new AvaloniaWindowsClipboard(_ => { }, ownerWindowFactory: () => IntPtr.Zero);
            try
            {
                failures += Check("no-owner clipboard write returns false", !noOwner.SetText("should not be written"));
                failures += Check("no-owner write leaves the clipboard intact", clipboard.TryGetText() == canary);
            }
            finally
            {
                noOwner.Dispose();
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(original))
            {
                clipboard.SetText(original);
            }
            else
            {
                // The clipboard started with no text (empty, or non-text data we
                // can't capture here). Clear our test text instead of leaving it
                // behind. Non-text formats are not preserved.
                clipboard.Clear();
                Console.WriteLine("  note: clipboard started with no text; cleared test text (non-text formats not preserved).");
            }

            clipboard.Dispose();
        }

        Console.WriteLine(failures == 0
            ? "All Zetl Windows self-tests passed."
            : $"{failures} Zetl Windows self-test(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static int Check(string name, bool passed)
    {
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
        return passed ? 0 : 1;
    }

    private sealed class StubHttpHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
