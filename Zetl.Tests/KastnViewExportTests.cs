using System.Text;
using KASTN;
using PdfSharp.Pdf.IO;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public class KastnViewExportTests
{
    [Fact]
    public void CapturesMutableViewFiltersAndPreferenceBeforeRendering()
    {
        var project = Project();
        var visible = project.Slips.ToList();
        var view = new ZetlViewDocument
        {
            Id = "view", Name = "Original", Kind = ZetlViewKinds.Html, Title = "Captured title",
            Sections = [new() { Title = "Captured section", Buckets = ["Ideas"] }]
        };
        var settings = new ZetlAppSettings { KastnPreferSlipKindOverBucketKind = false };
        var expected = ZetlViewRenderer.Render(project, visible, view, preferSlipKindOverBucketKind: false);
        Assert.NotEqual(expected, ZetlViewRenderer.Render(project, visible, view, preferSlipKindOverBucketKind: true));
        var operation = new KastnViewExportOperation(project, visible, view, settings);
        visible.Clear();
        view.Name = "Changed";
        view.Title = "Changed title";
        view.Kind = ZetlViewKinds.Pdf;
        view.Sections[0].Title = "Changed section";
        view.Sections[0].Buckets.Clear();
        settings.KastnPreferSlipKindOverBucketKind = true;

        Assert.Equal(expected, operation.RenderText(new Dictionary<string, ZetlPictureContent>()));
        Assert.Equal("Original", operation.ViewName);
        Assert.Equal("html", operation.FileExtension);
        Assert.Equal("Captured Project.html", operation.SuggestedFileName);
        Assert.False(operation.IsPdf);
    }

    [Theory]
    [InlineData(ZetlViewKinds.Formatted)]
    [InlineData(ZetlViewKinds.Plain)]
    [InlineData(ZetlViewKinds.Tsv)]
    public async Task LiteralFormatsDoNotFetchPictureBytes(string kind)
    {
        var project = Project() with { Slips = [Picture("image")] };
        var operation = Operation(project, kind);
        var pictures = await operation.LoadPicturesAsync((_, _) => throw new InvalidOperationException("No fetch needed."));
        Assert.Empty(pictures);
        Assert.Contains("Picture", operation.RenderText(pictures));
    }

    [Fact]
    public async Task PictureLoadsKeepOriginalProjectAndRejectChangedAssets()
    {
        var project = Project() with { Slips = [Picture("first"), Picture("second"), Picture("third")] };
        var operation = Operation(project, ZetlViewKinds.Html);
        var first = new TaskCompletionSource<ZetlPictureContent?>();
        var requested = new List<string>();
        var loading = operation.LoadPicturesAsync((projectId, slip) =>
        {
            Assert.Equal(project.Id, projectId);
            requested.Add(slip.Id);
            return slip.Id switch
            {
                "first" => first.Task,
                "second" => Task.FromResult<ZetlPictureContent?>(Content(slip.Id) with { Sha256 = "changed asset" }),
                _ => throw new IOException("Unavailable")
            };
        });
        Assert.Single(requested);
        first.SetResult(Content("first"));
        var pictures = await loading;
        Assert.Equal(new[] { "first", "second", "third" }, requested);
        Assert.Equal("first", Assert.Single(pictures).Key);
        var html = operation.RenderText(pictures);
        Assert.Contains("data:image/png;base64,AQID", html);
        Assert.Contains("[Picture: second]", html);
        Assert.Contains("[Picture: third]", html);
    }

    [Fact]
    public async Task PdfUsesCapturedPageAndFontSettings()
    {
        var project = Project();
        var settings = new ZetlAppSettings { PdfPageFormat = "A4", PdfFontSize = 17 };
        var operation = new KastnViewExportOperation(project, project.Slips,
            new() { Name = "PDF", Kind = ZetlViewKinds.Pdf }, settings);
        settings.PdfPageFormat = "Letter";
        settings.PdfFontSize = 8;
        using var stream = new MemoryStream();
        await operation.WriteAsync(stream, new Dictionary<string, ZetlPictureContent>());
        stream.Position = 0;
        using var pdf = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        var page = Assert.Single(pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>());
        Assert.InRange(page.Width.Point, 595, 596);
        Assert.InRange(page.Height.Point, 841, 843);
        var commands = Encoding.ASCII.GetString(page.Contents.CreateSingleContent().Stream.UnfilteredValue);
        Assert.Contains("17 Tf", commands);
    }

    internal static ZetlProjectSnapshot Project() => new()
    {
        Id = "original", Name = "Captured Project", ChangeSequence = 1, MetadataRevision = 1,
        Buckets = [new() { Id = "bucket", Name = "Ideas", Revision = 1, RenderKind = ZetlBlockKinds.Ordered }],
        Slips = [new()
        {
            Id = "text", BucketId = "bucket", Revision = 1, Type = ZetlSlipType.Text,
            Text = "captured text", BlockKind = ZetlBlockKinds.Task, Source = "copy", CapturedAtUtc = DateTimeOffset.UtcNow
        }]
    };

    internal static ZetlSlipSnapshot Picture(string id) => Project().Slips[0] with
    {
        Id = id, Text = id, Type = ZetlSlipType.Picture, BlockKind = "",
        Picture = new() { Sha256 = id, Width = 1, Height = 1, ByteLength = 3 }
    };

    internal static ZetlPictureContent Content(string id) => new()
    {
        SlipId = id, Sha256 = id, Width = 1, Height = 1, Bytes = [1, 2, 3]
    };

    private static KastnViewExportOperation Operation(ZetlProjectSnapshot project, string kind) =>
        new(project, project.Slips, new() { Name = kind, Kind = kind }, new());
}
