using System.Diagnostics;
using System.Text;

namespace ZETL;

/// <summary>
/// `Zetl --linux-clipboard-probe`: exercises Zetl's Linux clipboard against the
/// live desktop with no window, using xclip as the other X11 app and wl-copy /
/// wl-paste as a Wayland one. For checking a desktop (GNOME, an X11 session)
/// where the .NET test suite can't run. Set ZETL_LINUX_CLIPBOARD=x11 to force
/// the X11 path. It replaces the clipboard and puts its text back at the end.
/// </summary>
internal static class ZetlLinuxClipboardProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static int Run()
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("The Linux clipboard probe runs on Linux.");
            return 2;
        }

        void Log(string message) => Console.WriteLine($"  log: {message}");
        if (ZetlPlatformServices.ConnectLinuxSelection(Log) is not { } selection)
        {
            Console.WriteLine("FAIL no clipboard to probe");
            return 1;
        }

        using var clipboard = new LinuxClipboard(selection, ZetlClipboardImages.Normalize, Log);
        var failures = 0;
        void Check(string what, bool passed)
        {
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {what}");
            if (!passed) failures++;
        }

        bool WaitForChange(uint before)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (clipboard.GetChangeToken() == before)
            {
                if (DateTime.UtcNow > deadline) return false;
                Thread.Sleep(20);
            }

            return true;
        }

        var original = clipboard.TryGetText();
        Console.WriteLine($"Clipboard now: {Snip(original)} [{string.Join(", ", selection.GetMimeTypes(out _) ?? [])}]");
        if (Environment.GetEnvironmentVariable("ZETL_PROBE_WAIT") == "1")
        {
            // Read-only: waits for the next copy and captures it the moment it
            // lands, as a held copy does, timing each try.
            var waitFrom = clipboard.GetChangeToken();
            Console.WriteLine("Waiting up to 30 s for a copy...");
            var waitUntil = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (clipboard.GetChangeToken() == waitFrom && DateTime.UtcNow < waitUntil) Thread.Sleep(5);
            var sinceChange = Stopwatch.StartNew();
            if (Environment.GetEnvironmentVariable("ZETL_PROBE_TYPES") is { Length: > 0 } typeList)
            {
                // Chosen types one after another, as soon as the copy lands, and
                // what turning a non-PNG picture into Zetl's PNG costs.
                foreach (var type in typeList.Split(','))
                {
                    var timer = Stopwatch.StartNew();
                    var data = selection.Read([type], out _);
                    var read = timer.ElapsedMilliseconds;
                    var bytes = data?.GetValueOrDefault(type);
                    var normalize = Stopwatch.StartNew();
                    var image = bytes is null ? null : ZetlClipboardImages.Normalize(bytes, type);
                    Console.WriteLine($"{type} at +{sinceChange.ElapsedMilliseconds - read} ms: {bytes?.Length} bytes in {read} ms; "
                        + $"to PNG {image?.PngBytes.Length} bytes ({image?.Width}x{image?.Height}) in {normalize.ElapsedMilliseconds} ms");
                }

                return 0;
            }

            if (Environment.GetEnvironmentVariable("ZETL_PROBE_PAIR") == "1")
            {
                // Two captures at once, as Zetl's change observer and its
                // automatic capture both read on a copy.
                var pair = Enumerable.Range(1, 2).Select(index => Task.Run(() =>
                {
                    var timer = Stopwatch.StartNew();
                    var capture = clipboard.TryCaptureContent();
                    return $"pair {index}: capture in {timer.ElapsedMilliseconds} ms: "
                        + (capture is null ? "null" : $"text={capture.Text?.Length}, image={capture.Image?.PngBytes.Length}");
                })).ToArray();
                Task.WaitAll(pair);
                foreach (var line in pair) Console.WriteLine(line.Result);
                return 0;
            }

            for (var attempt = 1; attempt <= 4; attempt++)
            {
                var timer = Stopwatch.StartNew();
                var types = selection.GetMimeTypes(out _);
                var listed = timer.ElapsedMilliseconds;
                var capture = clipboard.TryCaptureContent();
                Console.WriteLine($"try {attempt} at +{sinceChange.ElapsedMilliseconds - timer.ElapsedMilliseconds} ms: types={types?.Count} in {listed} ms, capture in {timer.ElapsedMilliseconds} ms: "
                    + (capture is null ? "null" : $"text={capture.Text?.Length}, image={capture.Image?.PngBytes.Length}"));
                Thread.Sleep(500);
            }

            return 0;
        }

        if (Environment.GetEnvironmentVariable("ZETL_PROBE_RAW") == "1")
        {
            // Read-only: each offered type alone, timed, then the capture Zetl
            // makes on a held copy. Sizes only; content is never printed.
            foreach (var type in selection.GetMimeTypes(out _) ?? [])
            {
                var timer = Stopwatch.StartNew();
                var data = selection.Read([type], out _);
                Console.WriteLine($"{type}: {(data?.TryGetValue(type, out var bytes) == true ? $"{bytes.Length} bytes" : "nothing")} in {timer.ElapsedMilliseconds} ms");
            }

            var captureTimer = Stopwatch.StartNew();
            var capture = clipboard.TryCaptureContent();
            Console.WriteLine($"Capture in {captureTimer.ElapsedMilliseconds} ms: "
                + (capture is null ? "null" : $"text={capture.Text?.Length}, html={capture.Html?.Length}, image={capture.Image?.PngBytes.Length}, replay={capture.ReplayFormats?.Count}, files={capture.Files}"));
            return 0;
        }

        Check("Zetl's write is read by an X11 app",
            clipboard.ReplaceRichText("zetl probe ü ✓", "<b>zetl probe</b>").Succeeded
            && Xclip("-o") == "zetl probe ü ✓");
        Check("  it offers HTML too", Xclip("-t", "TARGETS", "-o").Contains("text/html"));
        Check("  and the HTML arrives", Xclip("-t", "text/html", "-o").Contains("<b>zetl probe</b>"));

        var before = clipboard.GetChangeToken();
        XclipCopy("from xclip ü");
        var changed = WaitForChange(before);
        var seen = clipboard.TryCaptureContent()?.Text;
        Check($"Zetl sees another X11 app's copy (changed={changed}, got {Snip(seen)})",
            changed && seen == "from xclip ü");

        var outgoing = new string('z', 1_000_000) + "|end";
        Check("Zetl sends 1 MB (incremental transfer)",
            clipboard.ReplaceText(outgoing).Succeeded && Xclip("-o") == outgoing);

        var incoming = new string('y', 1_000_000) + "|end";
        before = clipboard.GetChangeToken();
        XclipCopy(incoming);
        changed = WaitForChange(before);
        seen = clipboard.TryCaptureContent()?.Text;
        Check($"Zetl receives 1 MB (incremental transfer) (changed={changed}, got {Snip(seen)})",
            changed && seen == incoming);

        var staged = clipboard.StagePaste("staged probe", null, null, null);
        var stagedReadEarly = staged is not null && staged.Read.Wait(300) && staged.Read.Result;
        var pasted = Xclip("-o");
        Check("A staged paste reports the read that pasted",
            staged is not null && !stagedReadEarly && pasted == "staged probe"
            && staged.Read.Wait(Timeout) && staged.Read.Result);

        before = clipboard.GetChangeToken();
        XclipCopy("back me up");
        changed = WaitForChange(before);
        var backup = clipboard.CaptureBackup();
        var replaced = clipboard.ReplaceText("temporary").Succeeded;
        var restored = clipboard.RestoreBackup(backup);
        var after = Xclip("-o");
        Check($"A backup restores another app's clipboard (changed={changed}, complete={backup.IsComplete} "
            + $"{backup.FailureReason}, replaced={replaced}, restored={restored}, then {Snip(after)})",
            backup.IsComplete && replaced && restored && after == "back me up");

        // The Wayland side: whether the desktop mirrors X11 and Wayland copies.
        Console.WriteLine($"Wayland app (wl-paste) sees: {Snip(Run("wl-paste", ["--no-newline"], null))}");
        before = clipboard.GetChangeToken();
        Run("wl-copy", ["from wl-copy"], null, wait: false);
        var waylandSeen = WaitForChange(before) ? clipboard.TryCaptureContent()?.Text : null;
        Console.WriteLine($"Zetl sees a Wayland app's copy (wl-copy): {Snip(waylandSeen)}");

        // Zetl's ownership ends with this process; xclip keeps the text alive.
        if (original is not null) XclipCopy(original);
        Console.WriteLine(failures == 0 ? "All clipboard checks passed." : $"{failures} clipboard check(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static string Xclip(params string[] args) =>
        Run("xclip", ["-selection", "clipboard", .. args], null) ?? "";

    private static void XclipCopy(string text) =>
        Run("xclip", ["-selection", "clipboard", "-i"], text);

    private static string? Run(string tool, string[] args, string? input, bool wait = true)
    {
        try
        {
            var start = new ProcessStartInfo(tool)
            {
                UseShellExecute = false,
                RedirectStandardInput = input is not null,
                // Every stream is redirected: a tool that stays behind to serve
                // the selection (xclip -i, wl-copy) must not hold the probe's own
                // output (an SSH session, say) open after the probe exits.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            if (input is not null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }

            if (!wait || input is not null)
            {
                // The serving child keeps the pipes; only the launcher exits.
                process.WaitForExit(5000);
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            process.WaitForExit(5000);
            return output.Wait(1000) ? output.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"({tool} unavailable: {ex.Message})";
        }
    }

    private static string Snip(string? text) => text is null
        ? "(nothing)"
        : text.Length > 40 ? $"\"{text[..40]}…\" ({text.Length} chars)" : $"\"{text}\"";
}
