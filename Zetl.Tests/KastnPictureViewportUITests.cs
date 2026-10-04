using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaFact]
    public async Task SharedReaderBitmapStaysAliveUntilBothControlsReleaseIt()
    {
        using var pictures = new KastnPictureCache((_, _) => Task.FromResult<ZetlPictureContent?>(ReaderPictureContent()), decodedBudget: 0);
        using var first = new ReaderHarness(pictures: pictures);
        using var second = new ReaderHarness(pictures: pictures);
        var project = ReaderPictureProject();
        first.Render(project);
        await ReaderPictureLoad(first.Reader, "render-one").WaitAsync(TimeSpan.FromSeconds(5));
        var firstImage = Assert.Single(ContentControls(first.Reader.Blocks["render-one"]).OfType<Image>());
        var bitmap = firstImage.Source;
        Assert.NotNull(bitmap);
        second.Render(project with { Id = "second-reader" });
        var secondImage = Assert.Single(ContentControls(second.Reader.Blocks["render-one"]).OfType<Image>());
        Assert.Same(bitmap, secondImage.Source);
        first.Reader.Clear();
        Assert.Null(firstImage.Source);
        Assert.Same(bitmap, secondImage.Source);
        Assert.Equal(1, pictures.DecodedCount);
        Assert.Equal(pictures.DecodedResidentBytes, pictures.PinnedDecodedBytes);
        second.Reader.Clear();
        Assert.Null(secondImage.Source);
        Assert.Equal(0, pictures.DecodedResidentBytes);
        Assert.Equal(0, pictures.DecodedCount);
    }

    [AvaloniaFact]
    public async Task ReaderAndBoardReleaseTheirOwnWidthsWithoutEvictingDisplayedPictures()
    {
        using var pictures = new KastnPictureCache((_, _) => Task.FromResult<ZetlPictureContent?>(ReaderPictureContent()), decodedBudget: 0);
        using var reader = new ReaderHarness(pictures: pictures);
        using var board = new BoardHarness(pictures: pictures);
        var project = ReaderPictureProject();
        reader.Render(project);
        await ReaderPictureLoad(reader.Reader, "render-one").WaitAsync(TimeSpan.FromSeconds(5));
        board.Render(project);
        await BoardPictureLoad(board.Board, "render-one").WaitAsync(TimeSpan.FromSeconds(5));
        var boardImage = Assert.Single(ContentControls(board.Board.Card("render-one")!).OfType<Image>());
        var thumbnail = boardImage.Source;
        Assert.NotNull(thumbnail);
        Assert.Equal(2, pictures.DecodedCount);
        reader.Reader.Clear();
        Assert.Null(pictures.FindDecoded("asset", 1100));
        Assert.Same(thumbnail, boardImage.Source);
        Assert.Same(thumbnail, pictures.FindDecoded("asset", 260));
        board.Board.Clear();
        Assert.Null(boardImage.Source);
        Assert.Equal(0, pictures.DecodedResidentBytes);
    }

    [AvaloniaFact]
    public async Task NinetySixPictureReaderUsesViewportAndReleasesEvictedImageSources()
    {
        using var pictures = PictureViewportCache();
        using var h = new ReaderHarness(pictures: pictures);
        h.Render(PictureViewportProject());
        await SettlePictures(() => h.Reader.Blocks.Values.SelectMany(ContentControls).OfType<Image>(), h.Window);
        Assert.InRange(h.Reader.Blocks.Count, 1, 20);
        var firstImage = Assert.Single(ContentControls(h.Reader.Blocks["note-0"]).OfType<Image>());
        Assert.NotNull(firstImage.Source);
        h.Reader.UpdateSelection("note-95");
        h.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        await SettlePictures(() => h.Reader.Blocks.Values.SelectMany(ContentControls).OfType<Image>(), h.Window);
        Assert.NotNull(h.Reader.Blocks.GetValueOrDefault("note-95"));
        Assert.Null(firstImage.Source);
        Assert.InRange(h.Reader.Blocks.Count, 1, 20);
        Assert.InRange(pictures.DecodedCount, 1, h.Reader.Blocks.Count + 1);
        h.Reader.UpdateSelection("note-0");
        h.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        await SettlePictures(() => h.Reader.Blocks.Values.SelectMany(ContentControls).OfType<Image>(), h.Window);
        Assert.NotNull(Assert.Single(ContentControls(h.Reader.Blocks["note-0"]).OfType<Image>()).Source);
        h.Reader.Clear();
        await WaitForConditionAsync(() => pictures.DecodedResidentBytes == 0, "Reader leases should all retire.");
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NinetySixPictureBoardReleasesPictureAndDualPeekSourcesOnEviction(bool dual)
    {
        using var pictures = PictureViewportCache();
        using var h = new BoardHarness(pictures: pictures);
        h.Render(PictureViewportProject(dual));
        IEnumerable<Image> Images() => BoardRecords(h.Board, "boardCards").Keys.Cast<string>()
            .SelectMany(id => ContentControls(h.Board.Card(id)!)).OfType<Image>();
        await SettlePictures(Images, h.Window);
        Assert.InRange(h.Board.RealizedCardCount, 1, 32);
        var first = ContentControls(h.Board.Card("note-0")!).OfType<Image>().ToArray();
        Assert.All(first, image => Assert.NotNull(image.Source));
        if (dual) { PressContent(first[0]); Assert.True(first[1].IsVisible); }
        h.Board.UpdateSelection("note-95");
        h.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        await SettlePictures(Images, h.Window);
        Assert.NotNull(h.Board.Card("note-95"));
        Assert.All(first, image => Assert.Null(image.Source));
        Assert.InRange(h.Board.RealizedCardCount, 1, 32);
        Assert.InRange(pictures.DecodedCount, 1, h.Board.RealizedCardCount + 1);
        h.Board.UpdateSelection("note-0");
        h.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        await SettlePictures(Images, h.Window);
        var returned = ContentControls(h.Board.Card("note-0")!).OfType<Image>().ToArray();
        Assert.All(returned, image => Assert.NotNull(image.Source));
        if (dual) Assert.True(returned[1].IsVisible);
        h.Board.Clear();
        await WaitForConditionAsync(() => pictures.DecodedResidentBytes == 0, "Board leases should all retire.");
    }

    private static KastnPictureCache PictureViewportCache() => new((_, slip) =>
        Task.FromResult<ZetlPictureContent?>(ReaderPictureContent() with { Sha256 = slip.Picture!.Sha256, SlipId = slip.Id }), decodedBudget: 0);

    private static ZetlProjectSnapshot PictureViewportProject(bool dual = false)
    {
        var project = ViewportProject(96);
        return project with { Slips = project.Slips.Select(slip => slip with
        {
            Type = dual ? ZetlSlipType.Text : ZetlSlipType.Picture,
            Picture = new() { Sha256 = slip.Id, Width = 1, Height = 1 }
        }).ToArray() };
    }

    private static async Task SettlePictures(Func<IEnumerable<Image>> images, Window window)
    {
        await WaitForConditionAsync(() =>
        {
            window.UpdateLayout();
            var current = images().ToArray();
            return current.Length > 0 && current.All(image => image.Source is not null);
        }, "Live picture controls should finish loading.");
        window.UpdateLayout();
    }
}
