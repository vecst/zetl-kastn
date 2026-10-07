using System.Diagnostics;
using System.Text;
using Xunit;

namespace ZETL.Tests;

/// <summary>Runs only inside a Wayland session that has wl-clipboard's tools.</summary>
public sealed class WaylandClipboardFactAttribute : FactAttribute
{
    public WaylandClipboardFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            Skip = "Needs a Wayland session.";
        }
        else if (!File.Exists("/usr/bin/wl-copy") || !File.Exists("/usr/bin/wl-paste"))
        {
            Skip = "Needs wl-copy and wl-paste.";
        }
    }
}

/// <summary>
/// The Linux clipboard against the real compositor. Other apps are played by
/// wl-copy/wl-paste and by a second data-control connection. These tests
/// replace the session's clipboard; its text is put back afterwards.
/// </summary>
[Collection(RealTimeCollection.Name)]
public sealed class LinuxWaylandClipboardTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private readonly List<string> logs = [];
    private readonly WaylandDataControl? selection;
    private readonly LinuxClipboard? clipboard;
    private readonly string? originalText;

    public LinuxWaylandClipboardTests()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return;
        selection = WaylandDataControl.TryConnect(logs.Add);
        Assert.True(selection is not null, string.Join("\n", logs));
        clipboard = new LinuxClipboard(selection, (bytes, _) => new ZetlClipboardImage(bytes, 1, 1), logs.Add);
        originalText = clipboard.TryGetText();
    }

    public void Dispose()
    {
        clipboard?.Dispose();
        if (originalText is not null) Run("wl-copy", [originalText]);
    }

    [WaylandClipboardFact]
    public void ReadsWhatAnotherAppCopied()
    {
        var before = clipboard!.GetChangeToken();
        Run("wl-copy", ["Zetl probe ü ✓"]);

        WaitUntil(() => clipboard.GetChangeToken() != before, "a new selection generation");
        Assert.Equal("Zetl probe ü ✓", clipboard.TryCaptureContent()!.Text);
    }

    [WaylandClipboardFact]
    public void OtherAppsReadWhatZetlWrites()
    {
        Assert.True(clipboard!.ReplaceRichText("plain ü", "<b>rich</b>").Succeeded);

        var types = Run("wl-paste", ["--list-types"]);
        Assert.Contains("text/html", types);
        Assert.Contains("text/plain;charset=utf-8", types);
        Assert.Equal("plain ü", Run("wl-paste", ["--no-newline", "--type", "text/plain;charset=utf-8"]));
        Assert.Contains("<b>rich</b>", Run("wl-paste", ["--type", "text/html"]));
    }

    [WaylandClipboardFact]
    public void ZetlReadsASelectionItOwns()
    {
        Assert.True(clipboard!.ReplaceText("self-read").Succeeded);
        Assert.Equal("self-read", clipboard.TryCaptureContent()!.Text);
    }

    [WaylandClipboardFact]
    public async Task AStagedPasteReportsTheReadThatHappened()
    {
        var staged = clipboard!.StagePaste("staged note", null, null, null)!;
        Assert.NotNull(staged);
        await Task.Delay(300);
        Assert.False(staged.Read.IsCompleted, "Nothing has pasted yet; a monitor must not count as the paste.");

        Assert.Equal("staged note", Run("wl-paste", ["--no-newline", "--type", "text/plain;charset=utf-8"]));
        Assert.True(await staged.Read.WaitAsync(Timeout));

        var unread = clipboard.StagePaste("never pasted", null, null, null)!;
        Run("wl-copy", ["something else"]);
        Assert.False(await unread.Read.WaitAsync(Timeout));
    }

    [WaylandClipboardFact]
    public void APasswordManagersCopyIsNotCaptured()
    {
        using var passwordManager = WaylandDataControl.TryConnect(logs.Add)!;
        var before = clipboard!.GetChangeToken();
        Assert.NotNull(passwordManager.Offer(new Dictionary<string, byte[]>
        {
            ["text/plain;charset=utf-8"] = "hunter2"u8.ToArray(),
            [LinuxClipboard.PasswordHint] = "secret"u8.ToArray()
        }));

        WaitUntil(() => clipboard.GetChangeToken() != before, "the password manager's selection");
        var snapshot = clipboard.TryCaptureContent()!;
        Assert.True(snapshot.Private);
        Assert.Null(snapshot.Text);
    }

    [WaylandClipboardFact]
    public void ABackupRestoresEveryTypeExactly()
    {
        using var otherApp = WaylandDataControl.TryConnect(logs.Add)!;
        var before = clipboard!.GetChangeToken();
        Assert.NotNull(otherApp.Offer(new Dictionary<string, byte[]>
        {
            ["text/plain;charset=utf-8"] = "original"u8.ToArray(),
            ["application/x-zetl-test"] = [1, 2, 3, 0, 255]
        }));
        WaitUntil(() => clipboard.GetChangeToken() != before, "the other app's selection");

        var backup = clipboard.CaptureBackup();
        Assert.True(backup.IsComplete, backup.FailureReason);
        Assert.True(clipboard.ReplaceText("temporary").Succeeded);
        Assert.True(clipboard.RestoreBackup(backup));

        Assert.Equal("original", Run("wl-paste", ["--no-newline", "--type", "text/plain;charset=utf-8"]));
        Assert.Equal(new byte[] { 1, 2, 3, 0, 255 }, RunBytes("wl-paste", ["--type", "application/x-zetl-test"]));
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

    private static string Run(string tool, string[] args) => Encoding.UTF8.GetString(RunBytes(tool, args));

    private static byte[] RunBytes(string tool, string[] args)
    {
        // wl-copy leaves a background process serving the selection; it must not
        // inherit a redirected stdout, or reading it would wait for that process.
        var reads = tool == "wl-paste";
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = reads, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var output = new MemoryStream();
        if (reads) process.StandardOutput.BaseStream.CopyTo(output);
        Assert.True(process.WaitForExit(5000), $"{tool} did not finish.");
        return output.ToArray();
    }
}
