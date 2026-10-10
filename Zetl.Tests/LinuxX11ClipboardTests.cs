using System.Diagnostics;
using System.Text;
using Xunit;

namespace ZETL.Tests;

/// <summary>Runs only with an X display (an X11 session, or Xwayland) and xclip.</summary>
public sealed class X11ClipboardFactAttribute : FactAttribute
{
    public X11ClipboardFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Skip = "Needs an X display.";
        }
        else if (!File.Exists("/usr/bin/xclip"))
        {
            Skip = "Needs xclip.";
        }
    }
}

/// <summary>
/// The Linux clipboard over X11's CLIPBOARD selection (X11 sessions, and GNOME
/// through Xwayland), against the real X server, with xclip as the other app.
/// These tests replace the clipboard; its text is put back afterwards.
/// </summary>
[Collection(RealTimeCollection.Name)]
public sealed class LinuxX11ClipboardTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private readonly List<string> logs = [];
    private readonly LinuxClipboard? clipboard;
    private readonly string? originalText;

    public LinuxX11ClipboardTests()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return;
        var selection = X11Selection.TryConnect(logs.Add);
        Assert.True(selection is not null, string.Join("\n", logs));
        clipboard = new LinuxClipboard(selection!, (bytes, _) => new ZetlClipboardImage(bytes, 1, 1), logs.Add);
        originalText = clipboard.TryGetText();
    }

    public void Dispose()
    {
        clipboard?.Dispose();
        // Zetl's ownership ends with the connection; xclip keeps the text alive.
        if (originalText is not null) Copy(originalText);
    }

    [X11ClipboardFact]
    public void ReadsWhatAnotherAppCopied()
    {
        var before = clipboard!.GetChangeToken();
        Copy("Zetl X11 probe ü ✓");
        WaitUntil(() => clipboard.GetChangeToken() != before, "a new selection generation");
        Assert.Equal("Zetl X11 probe ü ✓", clipboard.TryCaptureContent()!.Text);
    }

    [X11ClipboardFact]
    public void OtherAppsReadWhatZetlWrites()
    {
        Assert.True(clipboard!.ReplaceRichText("plain ü", "<b>rich</b>").Succeeded);
        Assert.Contains("text/html", Paste("TARGETS"));
        Assert.Equal("plain ü", Paste("UTF8_STRING"));
        Assert.Contains("<b>rich</b>", Paste("text/html"));
    }

    [X11ClipboardFact]
    public void ZetlReadsASelectionItOwns()
    {
        Assert.True(clipboard!.ReplaceText("self-read").Succeeded);
        Assert.Equal("self-read", clipboard.TryCaptureContent()!.Text);
    }

    [X11ClipboardFact]
    public void LargeTransfersGoBothWays()
    {
        // Over 128 KiB Zetl sends incrementally (INCR); xclip does the same.
        var outgoing = new string('z', 1_000_000) + "|end";
        Assert.True(clipboard!.ReplaceText(outgoing).Succeeded);
        Assert.Equal(outgoing, Paste("UTF8_STRING"));

        var incoming = new string('y', 1_000_000) + "|end";
        var before = clipboard.GetChangeToken();
        Copy(incoming);
        WaitUntil(() => clipboard.GetChangeToken() != before, "the large copy");
        Assert.Equal(incoming, clipboard.TryCaptureContent()!.Text);
    }

    [X11ClipboardFact]
    public async Task AStagedPasteReportsTheReadThatHappened()
    {
        var staged = clipboard!.StagePaste("staged note", null, null, null)!;
        Assert.NotNull(staged);
        await Task.Delay(300);
        Assert.False(staged.Read.IsCompleted, "Nothing has pasted yet.");

        Assert.Equal("staged note", Paste("UTF8_STRING"));
        Assert.True(await staged.Read.WaitAsync(Timeout));

        var unread = clipboard.StagePaste("never pasted", null, null, null)!;
        Copy("something else");
        Assert.False(await unread.Read.WaitAsync(Timeout));
    }

    [X11ClipboardFact]
    public void ABackupRestoresAnotherAppsClipboard()
    {
        var before = clipboard!.GetChangeToken();
        Copy("original");
        WaitUntil(() => clipboard.GetChangeToken() != before, "the other app's selection");

        var backup = clipboard.CaptureBackup();
        Assert.True(backup.IsComplete, backup.FailureReason);
        Assert.True(clipboard.ReplaceText("temporary").Succeeded);
        Assert.True(clipboard.RestoreBackup(backup));
        Assert.Equal("original", Paste("UTF8_STRING"));
    }

    private void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what}. Log:\n{string.Join("\n", logs)}");
            Thread.Sleep(20);
        }
    }

    // xclip -i stays behind to serve the selection; every stream is redirected
    // so it holds none of the test runner's.
    private static void Copy(string text)
    {
        using var process = Start("-i");
        process.StandardInput.Write(text);
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(5000), "xclip -i did not hand off.");
    }

    private static string Paste(string target)
    {
        using var process = Start("-t", target, "-o");
        var output = process.StandardOutput.ReadToEndAsync();
        Assert.True(process.WaitForExit(5000), "xclip -o did not finish.");
        return output.Result;
    }

    private static Process Start(params string[] args)
    {
        var start = new ProcessStartInfo("xclip")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-selection");
        start.ArgumentList.Add("clipboard");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }
}
