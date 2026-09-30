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
        var original = clipboard.CaptureBackup();
        var canRestoreCallerClipboard = original.IsComplete;
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
            failures += Check(
                "clipboard validates PNG snapshots without recompressing",
                AvaloniaWindowsClipboard.TryCreatePngSnapshot(
                    png,
                    out var pngSnapshot)
                && pngSnapshot is { Width: 1, Height: 1 }
                && pngSnapshot.PngBytes.SequenceEqual(png)
                && !AvaloniaWindowsClipboard.TryCreatePngSnapshot(
                    png[..24],
                    out _));

            failures += Check(
                "clipboard enhanced metafile covers its synthesized legacy format",
                AvaloniaWindowsClipboard.IsSynthesizedFormatCovered(
                    3, // CF_METAFILEPICT
                    [new ZetlClipboardFormatData(14, [1])])); // CF_ENHMETAFILE
            failures += Check(
                "clipboard DIB covers synthesized bitmap and palette handles",
                AvaloniaWindowsClipboard.IsSynthesizedFormatCovered(
                    2, // CF_BITMAP
                    [new ZetlClipboardFormatData(8, [1])]) // CF_DIB
                && AvaloniaWindowsClipboard.IsSynthesizedFormatCovered(
                    9, // CF_PALETTE
                    [new ZetlClipboardFormatData(17, [1])])); // CF_DIBV5
            failures += Check(
                "clipboard unsupported handles are not treated as synthesized",
                !AvaloniaWindowsClipboard.IsSynthesizedFormatCovered(
                    128, // CF_OWNERDISPLAY
                    [new ZetlClipboardFormatData(14, [1])]));

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

            if (canRestoreCallerClipboard)
            {
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

                var richText = "formatted clipboard text";
                var richHtml = "<p><strong>formatted</strong> clipboard text</p>";
                failures += Check(
                    "clipboard writes rich text with a plain fallback",
                    clipboard.SetRichText(richText, richHtml));
                failures += Check(
                    "clipboard reads back the rich HTML fragment",
                    clipboard.TryGetHtml() == richHtml);
                var richBackup = clipboard.CaptureBackup();
                failures += Check(
                    "clipboard backup captures both Unicode and HTML formats",
                    richBackup is { IsComplete: true, RawFormats: not null }
                    && richBackup.RawFormats.Any(item =>
                        item.Format == AvaloniaWindowsClipboard.UnicodeTextFormat)
                    && richBackup.RawFormats.Any(item =>
                        item.Format == AvaloniaWindowsClipboard.HtmlClipboardFormat));
                failures += Check(
                    "clipboard can be replaced before an exact restore",
                    clipboard.SetText("temporary replacement"));
                failures += Check(
                    "clipboard restores every rich format",
                    clipboard.RestoreBackup(richBackup));
                failures += Check(
                    "restored rich clipboard matches its exact backup",
                    BackupsEqual(richBackup, clipboard.CaptureBackup()));

                // Use the known-decodable source fixture for clipboard
                // round-trips. Resolver normalization has its own checks above.
                var liveImage = new ZetlClipboardImage(png, 1, 1);
                failures += Check(
                    "clipboard writes every requested image format",
                    clipboard.ReplaceImage(liveImage).Succeeded);
                var imageBackup = clipboard.CaptureBackup();
                failures += Check(
                    "clipboard round-trips exact native image payloads",
                    imageBackup is { IsComplete: true, RawFormats: not null }
                    && imageBackup.RawFormats.Any(item =>
                        item.Format == AvaloniaWindowsClipboard.PngClipboardFormat
                        && item.Data.SequenceEqual(liveImage.PngBytes))
                    && imageBackup.RawFormats.Any(item =>
                        item.Format == AvaloniaWindowsClipboard.DibClipboardFormat));
                failures += Check(
                    "clipboard image backup contains PNG and DIB",
                    imageBackup is { IsComplete: true, RawFormats: not null }
                    && imageBackup.RawFormats.Any(item =>
                        item.Format == AvaloniaWindowsClipboard.PngClipboardFormat)
                    && imageBackup.RawFormats.Any(item =>
                        item.Format == AvaloniaWindowsClipboard.DibClipboardFormat));

                var mixedFormats = richBackup.RawFormats!
                    .Concat(imageBackup.RawFormats ?? [])
                    .GroupBy(item => item.Format)
                    .Select(group => group.First())
                    .ToList();
                failures += Check(
                    "clipboard restores mixed text, rich, and image formats",
                    clipboard.ReplaceWithBackup(
                        ZetlClipboardBackup.FromRaw(mixedFormats)).Succeeded);
                var mixedBackup = clipboard.CaptureBackup();
                var mixedCapture = clipboard.TryCaptureContent();
                failures += Check(
                    "clipboard preserves every mixed format",
                    clipboard.TryGetText() == richText
                    && clipboard.TryGetHtml() == richHtml
                    && mixedBackup is { IsComplete: true, RawFormats: not null }
                    && mixedBackup.RawFormats.Any(item =>
                        item.Format == AvaloniaWindowsClipboard.PngClipboardFormat
                        && item.Data.SequenceEqual(liveImage.PngBytes))
                    && mixedBackup.RawFormats.Any(item =>
                        item.Format == AvaloniaWindowsClipboard.DibClipboardFormat));
                failures += Check(
                    "clipboard captures a mixed-content generation",
                    mixedCapture is not null);
                failures += Check(
                    "clipboard mixed capture records its observed sequence",
                    mixedCapture?.ChangeToken == clipboard.GetChangeToken());
                failures += Check(
                    "clipboard mixed capture includes text",
                    mixedCapture?.Text == richText);
                failures += Check(
                    "clipboard mixed capture includes rich HTML",
                    mixedCapture?.Html == richHtml);
                failures += Check(
                    "clipboard mixed capture includes an image",
                    mixedCapture?.Image is not null);
                failures += Check(
                    "clipboard reads the mixed image independently",
                    clipboard.TryGetImage() is not null);

                failures += Check(
                    "clipboard transaction can write an exactly empty clipboard",
                    clipboard.ReplaceWithBackup(
                        ZetlClipboardBackup.FromRaw([])).Succeeded
                    && clipboard.CaptureBackup() is
                    {
                        IsComplete: true,
                        RawFormats.Count: 0
                    });

                var calcFormats = richBackup.RawFormats!.ToList();
                calcFormats.Add(new ZetlClipboardFormatData(
                    0,
                    [1, 2, 3, 4],
                    "Star Embed Source (XML)"));
                calcFormats.Add(new ZetlClipboardFormatData(
                    0,
                    [5, 6, 7, 8],
                    "Star Object Descriptor (XML)"));
                failures += Check(
                    "clipboard stages named Calc-native formats",
                    clipboard.RestoreBackup(ZetlClipboardBackup.FromRaw(calcFormats)));
                var capturedCalcFormats = clipboard.TryGetReplayFormats();
                failures += Check(
                    "clipboard captures an allowlisted Calc-native Replay bundle",
                    capturedCalcFormats is { Count: >= 3 }
                    && capturedCalcFormats.Any(item =>
                        item.RegisteredName == "Star Embed Source (XML)"
                        && item.Data.SequenceEqual(new byte[] { 1, 2, 3, 4 }))
                    && capturedCalcFormats.Any(item =>
                        item.RegisteredName == "Star Object Descriptor (XML)"
                        && item.Data.SequenceEqual(new byte[] { 5, 6, 7, 8 })));

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

                // A clipboard holding a format Windows will not hand over (a
                // browser's virtual-file image) cannot be backed up. Writes must
                // still replace it rather than refusing until the user copies
                // something else.
                failures += Check(
                    "clipboard places an unreadable delay-rendered format",
                    clipboard.PlaceUnreadableFormatForSelfTest("Zetl Self-Test Unreadable"));
                failures += Check(
                    "an unreadable clipboard cannot be backed up",
                    !clipboard.CaptureBackup().IsComplete);
                failures += Check(
                    "a write over an unreadable clipboard still succeeds",
                    clipboard.SetText("written over unreadable"));
                failures += Check(
                    "the write replaced the unreadable clipboard",
                    clipboard.TryGetText() == "written over unreadable");
            }
            else
            {
                Console.WriteLine(
                    $"SKIP live clipboard mutation tests: caller clipboard could not be backed up safely ({original.FailureReason}).");
            }
        }
        finally
        {
            if (canRestoreCallerClipboard)
            {
                failures += Check(
                    "clipboard self-test restores the caller's exact clipboard",
                    clipboard.RestoreBackup(original));
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

    private static bool BackupsEqual(ZetlClipboardBackup left, ZetlClipboardBackup right)
    {
        if (!left.IsComplete
            || !right.IsComplete
            || left.RawFormats is null
            || right.RawFormats is null
            || left.RawFormats.Count != right.RawFormats.Count)
        {
            return false;
        }

        var rightByFormat = right.RawFormats.ToDictionary(item => item.Format);
        return left.RawFormats.All(item =>
            rightByFormat.TryGetValue(item.Format, out var other)
            && item.Data.SequenceEqual(other.Data));
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
