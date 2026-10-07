using System.Text;
using Xunit;

namespace ZETL.Tests;

// LinuxClipboard's decisions over a scripted selection; no Wayland involved.
public sealed class LinuxClipboardTests
{
    private readonly FakeSelection selection = new();
    private readonly List<string> logs = [];

    private LinuxClipboard CreateClipboard() =>
        new(selection, (bytes, type) => new ZetlClipboardImage(bytes, 1, 1), logs.Add);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void CaptureReadsThePreferredTypesFromOneGeneration()
    {
        selection.Set(new()
        {
            ["STRING"] = Encoding.Latin1.GetBytes("latin"),
            ["text/plain;charset=utf-8"] = Utf8("naïve text\0"),
            ["text/html"] = Utf8("<meta http-equiv=\"content-type\" content=\"text/html; charset=utf-8\"><b>bold</b>"),
            ["image/bmp"] = [1],
            ["image/png"] = [2],
            ["TARGETS"] = [],
        });

        var snapshot = CreateClipboard().TryCaptureContent();

        Assert.NotNull(snapshot);
        Assert.Equal(selection.Generation, snapshot.ChangeToken);
        Assert.Equal("naïve text", snapshot.Text);
        Assert.Equal("<b>bold</b>", snapshot.Html);
        Assert.Equal(new byte[] { 2 }, snapshot.Image!.PngBytes);
        Assert.Null(snapshot.ReplayFormats);
        Assert.Single(selection.Reads);
        Assert.Equal(new[] { "text/plain;charset=utf-8", "text/html", "image/png" }, selection.Reads[0]);
    }

    [Fact]
    public void PrivateContentIsReportedWithoutReadingIt()
    {
        selection.Set(new()
        {
            ["text/plain;charset=utf-8"] = Utf8("hunter2"),
            [LinuxClipboard.PasswordHint] = Utf8("secret"),
        });
        var clipboard = CreateClipboard();

        var snapshot = clipboard.TryCaptureContent();

        Assert.True(snapshot!.Private);
        Assert.Null(snapshot.Text);
        Assert.Empty(selection.Reads);
        Assert.True(clipboard.IsMarkedPrivate());
    }

    [Fact]
    public void ACopyLandingMidReadIsRetriedNotTorn()
    {
        selection.Set(new() { ["text/plain"] = Utf8("first") });
        selection.BeforeNextRead = () => selection.Set(new() { ["text/plain"] = Utf8("second") });

        var snapshot = CreateClipboard().TryCaptureContent();

        Assert.Equal("second", snapshot!.Text);
        Assert.Equal(selection.Generation, snapshot.ChangeToken);
    }

    [Fact]
    public void RepeatedCapturesOfOneGenerationShareASnapshot()
    {
        selection.Set(new() { ["text/plain"] = Utf8("same") });
        var clipboard = CreateClipboard();

        Assert.Same(clipboard.TryCaptureContent(), clipboard.TryCaptureContent());
        Assert.Single(selection.Reads);
    }

    [Theory]
    [InlineData("<html><body><!--StartFragment--><i>x</i><!--EndFragment--></body></html>", "<i>x</i>")]
    [InlineData("<meta charset='utf-8'><span>chrome</span>", "<span>chrome</span>")]
    [InlineData("<html><head></head><body class=\"a\"><p>doc</p></body></html>", "<p>doc</p>")]
    [InlineData("<p>bare</p>", "<p>bare</p>")]
    public void HtmlFragmentsMatchWhatWindowsCaptures(string html, string fragment) =>
        Assert.Equal(fragment, LinuxClipboard.ExtractHtmlFragment(html));

    [Fact]
    public void Utf16HtmlWithAByteOrderMarkIsDecoded()
    {
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("<b>ü</b>")).ToArray();
        Assert.Equal("<b>ü</b>", LinuxClipboard.DecodeHtml(bytes));
    }

    [Fact]
    public void CalcCopiesKeepTheirNativeSourceForReplay()
    {
        const string embed = "application/x-openoffice-embed-source-xml;windows_formatname=\"Star Embed Source (XML)\"";
        selection.Set(new()
        {
            ["text/plain;charset=utf-8"] = Utf8("1\t2"),
            ["text/html"] = Utf8("<table></table>"),
            [embed] = [9, 9],
        });

        var bundle = CreateClipboard().TryCaptureContent()!.ReplayFormats;

        Assert.NotNull(bundle);
        Assert.Equal(["text/plain;charset=utf-8", "text/html", embed], bundle.Select(f => f.RegisteredName));
    }

    [Fact]
    public void WritesOfferEveryTextTypeAndCharsetMarkedHtml()
    {
        var clipboard = CreateClipboard();

        Assert.True(clipboard.ReplaceRichText("plain", "<b>rich</b>").Succeeded);

        var offered = selection.Offers.Single().Data;
        Assert.All(LinuxClipboard.TextTypes, type => Assert.Equal(Utf8("plain"), offered[type]));
        Assert.EndsWith("<b>rich</b>", Encoding.UTF8.GetString(offered["text/html"]));
        Assert.Contains("charset=utf-8", Encoding.UTF8.GetString(offered["text/html"]));
    }

    [Fact]
    public void AFailedWriteLeavesTheClipboardAndSaysSo()
    {
        selection.Available = false;
        var result = CreateClipboard().ReplaceText("x");

        Assert.Equal(ZetlClipboardWriteStatus.ClipboardUnavailable, result.Status);
        Assert.True(result.ClipboardPreserved);
    }

    [Fact]
    public void BackupCopiesEveryContentTypeAndRestoresExactly()
    {
        selection.Set(new()
        {
            ["text/plain"] = Utf8("t"),
            ["application/x-custom"] = [1, 2, 3],
            ["SAVE_TARGETS"] = [],
        });
        var clipboard = CreateClipboard();

        var backup = clipboard.CaptureBackup();
        selection.Set(new() { ["text/plain"] = Utf8("other") });
        Assert.True(clipboard.RestoreBackup(backup));

        Assert.True(backup.IsComplete);
        Assert.Equal(["text/plain", "application/x-custom"], backup.RawFormats!.Select(f => f.RegisteredName));
        Assert.Equal(new byte[] { 1, 2, 3 }, selection.Offers.Last().Data["application/x-custom"]);
        Assert.False(selection.Offers.Last().Data.ContainsKey("SAVE_TARGETS"));
    }

    [Fact]
    public void BackupIsIncompleteWhenTheOwnerWithholdsAType()
    {
        selection.Set(new() { ["text/plain"] = Utf8("t"), ["application/x-slow"] = [1] });
        selection.Withheld.Add("application/x-slow");

        var backup = CreateClipboard().CaptureBackup();

        Assert.False(backup.IsComplete);
        Assert.Equal(ZetlClipboardWriteStatus.BackupIncomplete, CreateClipboard().ReplaceWithBackup(backup).Status);
    }

    [Fact]
    public void AnEmptyClipboardBacksUpAsEmptyAndRestoresByClearing()
    {
        var clipboard = CreateClipboard();
        var backup = clipboard.CaptureBackup();
        selection.Set(new() { ["text/plain"] = Utf8("x") });

        Assert.Empty(backup.RawFormats!);
        Assert.True(clipboard.RestoreBackup(backup));
        Assert.Empty(selection.Types);
    }

    [Fact]
    public async Task StagedPasteIsPrivateAndCountsOnlyContentReads()
    {
        var clipboard = CreateClipboard();
        var staged = clipboard.StagePaste("note", "<i>note</i>", null, null)!;
        var offer = selection.Offers.Single();

        Assert.Equal(selection.Generation, staged.ChangeToken);
        Assert.Equal(Utf8("secret"), offer.Data[LinuxClipboard.PasswordHint]);
        Assert.Equal(Utf8("note"), offer.Data["text/plain;charset=utf-8"]);
        Assert.True(offer.Data.ContainsKey("text/html"));

        offer.Sent!(LinuxClipboard.PasswordHint);
        Assert.False(staged.Read.IsCompleted);
        offer.Sent!("text/plain;charset=utf-8");
        Assert.True(await staged.Read);
    }

    [Fact]
    public void MonitorsReadingANewSelectionDoNotCountAsThePaste()
    {
        selection.MonitorReadsOnOffer.Add("text/plain;charset=utf-8");
        var staged = CreateClipboard().StagePaste("note", null, null, null)!;
        Assert.False(staged.Read.IsCompleted);
    }

    [Fact]
    public async Task AStagedPasteReplacedBeforeAnyReadReportsSo()
    {
        var staged = CreateClipboard().StagePaste("note", null, null, null)!;
        selection.Offers.Single().Replaced!();
        Assert.False(await staged.Read);
    }

    [Fact]
    public void StagedPicturesOfferPngAndLinuxReplayBundlesKeepTheirTypes()
    {
        var clipboard = CreateClipboard();
        clipboard.StagePaste("alt", null, null, new ZetlClipboardImage([7], 1, 1));
        Assert.Equal(new byte[] { 7 }, selection.Offers.Last().Data["image/png"]);

        clipboard.StagePaste("1\t2", null,
            [new ZetlClipboardFormatData(0, [5], "application/x-openoffice-embed-source-xml"),
             new ZetlClipboardFormatData(0, Utf8("1\t2"), "text/plain;charset=utf-8")],
            null);
        var data = selection.Offers.Last().Data;
        Assert.Equal(new byte[] { 5 }, data["application/x-openoffice-embed-source-xml"]);
        Assert.False(data.ContainsKey("UTF8_STRING"), "A bundle with its own text is offered as captured.");
    }

    private sealed class FakeSelection : ILinuxSelection
    {
        private Dictionary<string, byte[]> content = [];

        public uint Generation { get; private set; } = 1;

        public bool Available { get; set; } = true;

        public List<string[]> Reads { get; } = [];

        public HashSet<string> Withheld { get; } = [];

        public List<(IReadOnlyDictionary<string, byte[]> Data, Action<string>? Sent, Action? Replaced)> Offers { get; } = [];

        public Action? BeforeNextRead { get; set; }

        /// <summary>Types a clipboard monitor reads the moment a selection appears.</summary>
        public List<string> MonitorReadsOnOffer { get; } = [];

        public IReadOnlyList<string> Types => content.Keys.ToList();

        public void Set(Dictionary<string, byte[]> data)
        {
            content = data;
            Generation++;
        }

        public IReadOnlyList<string>? GetMimeTypes(out uint generation)
        {
            generation = Generation;
            return Available ? content.Keys.ToList() : null;
        }

        public IReadOnlyDictionary<string, byte[]>? Read(IReadOnlyCollection<string> mimeTypes, out uint generation)
        {
            if (BeforeNextRead is { } before)
            {
                BeforeNextRead = null;
                before();
            }

            generation = Generation;
            if (!Available) return null;
            Reads.Add(mimeTypes.ToArray());
            return mimeTypes
                .Where(type => content.ContainsKey(type) && !Withheld.Contains(type))
                .ToDictionary(type => type, type => content[type]);
        }

        public uint? Offer(IReadOnlyDictionary<string, byte[]> data, Action<string>? sent = null, Action? replaced = null)
        {
            if (!Available) return null;
            Offers.Add((data, sent, replaced));
            Set(data.ToDictionary(pair => pair.Key, pair => pair.Value));
            foreach (var type in MonitorReadsOnOffer) sent?.Invoke(type);
            return Generation;
        }

        public uint? Clear()
        {
            if (!Available) return null;
            Set([]);
            return Generation;
        }
    }
}
