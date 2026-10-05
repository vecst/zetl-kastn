using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    private static ZetlProjectSnapshot ManyBucketProject(int buckets = 1000, int perBucket = 1, bool grouped = false)
    {
        var project = ViewportProject(buckets * perBucket);
        return project with
        {
            Buckets = Enumerable.Range(0, buckets).Select(i => project.Buckets[0] with
            {
                Id = $"bucket-{i}", Name = $"Bucket {i}", RenderKind = grouped ? "group" : "plain"
            }).ToArray(),
            Slips = project.Slips.Select((s, i) => s with { BucketId = $"bucket-{i / perBucket}" }).ToArray()
        };
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManyBucketReaderBoundsHeadingsAndGroupsAndNavigatesToDistantNotes(bool grouped)
    {
        using var h = new ReaderHarness();
        var project = ManyBucketProject(grouped: grouped);
        h.Render(project);
        SettleReader(h);
        Assert.InRange(h.Reader.RealizedHeadingCount, 1, 25);
        Assert.InRange(h.Reader.Blocks.Count, 1, 25);
        var old = h.Reader.Blocks["note-0"];
        h.Reader.UpdateSelection("note-999");
        SettleReader(h);
        Assert.Contains("note-999", h.Reader.Blocks.Keys);
        Assert.DoesNotContain("note-0", h.Reader.Blocks.Keys);
        Assert.InRange(h.Reader.RealizedHeadingCount, 1, 25);
        PressContent(old);
        Assert.Empty(h.Selected);
        h.Reader.UpdateSelection("note-0");
        SettleReader(h);
        Assert.Contains("note-0", h.Reader.Blocks.Keys);
        Assert.True(h.Scroll.Offset.Y < h.Scroll.Viewport.Height);
        h.Window.Height = 500;
        SettleReader(h);
        Assert.InRange(h.Reader.RealizedHeadingCount, 1, 35);
        Assert.InRange(h.Reader.Blocks.Count, 1, 35);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManyBucketReaderAlsoVirtualizesNotesInsideLargeSections(bool grouped)
    {
        using var h = new ReaderHarness();
        h.Render(ManyBucketProject(64, 100, grouped));
        SettleReader(h);
        Assert.InRange(h.Reader.Blocks.Count, 1, 100);
        h.Reader.UpdateSelection("note-6399");
        SettleReader(h);
        Assert.Contains("note-6399", h.Reader.Blocks.Keys);
        Assert.InRange(h.Reader.Blocks.Count, 1, 100);
        h.Reader.UpdateSelection("note-0");
        SettleReader(h);
        Assert.Contains("note-0", h.Reader.Blocks.Keys);
        Assert.True(h.Scroll.Offset.Y < h.Scroll.Viewport.Height);
    }

    [AvaloniaFact]
    public void ManyBucketReaderKeepsFreshHeadingsContentAndAnchorAcrossBucketRemovalAndThresholdChanges()
    {
        using var h = new ReaderHarness();
        var project = ManyBucketProject(100);
        h.Render(project);
        h.Reader.UpdateSelection("note-50");
        SettleReader(h);
        var anchor = h.Reader.Blocks.Select(pair => (pair.Key, Y: pair.Value.TranslatePoint(default, h.Scroll)!.Value.Y,
                Height: pair.Value.Bounds.Height)).Where(row => row.Y + row.Height > 0 && row.Y < h.Scroll.Viewport.Height)
            .OrderBy(row => row.Y).First();
        project = project with
        {
            ChangeSequence = 2,
            Buckets = project.Buckets.Skip(1).Select(b => b.Id == "bucket-50" ? b with { Name = "Renamed heading", Revision = 2 } : b).ToArray(),
            Slips = project.Slips.Skip(1).Select(s => s.Id == "note-99" ? s with { Text = "Edited distant note", Revision = 2 } : s).ToArray()
        };
        h.Render(project, selected: "note-50");
        SettleReader(h);
        Assert.Contains("note-50", h.Reader.Blocks.Keys);
        Assert.InRange(Math.Abs(h.Reader.Blocks[anchor.Key].TranslatePoint(default, h.Scroll)!.Value.Y - anchor.Y), 0, 1);
        Assert.Contains(h.Panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Renamed heading");
        h.Reader.UpdateSelection("note-99");
        SettleReader(h);
        Assert.Contains(ContentControls(h.Reader.Blocks["note-99"]).OfType<TextBlock>(), t => t.Inlines?.Text == "Edited distant note");
        h.Render(project with { ChangeSequence = 3, Buckets = project.Buckets.Take(2).ToArray(), Slips = project.Slips.Take(2).ToArray() });
        SettleReader(h);
        Assert.Equal(2, h.Reader.Blocks.Count);
        Assert.Equal(2, h.Reader.RealizedHeadingCount);
        h.Render(ManyBucketProject() with { Id = "other" });
        SettleReader(h);
        Assert.InRange(h.Reader.RealizedHeadingCount, 1, 25);
        Assert.True(h.Scroll.Offset.Y < h.Scroll.Viewport.Height);
    }

    [AvaloniaFact]
    public void ManyBucketBoardBoundsShellsAndRetiresTheirDragAndComposerActions()
    {
        using var h = new BoardHarness();
        h.Render(ManyBucketProject());
        SettleBoard(h);
        Assert.InRange(h.Board.RealizedColumnCount, 1, 6);
        Assert.InRange(h.Panel.Children.Count, 1, 8);
        var oldComposer = h.Composer("bucket-0");
        var oldCard = h.Board.Card("note-0")!;
        var oldHeader = (Control)oldComposer.Button.Parent!;
        h.Board.UpdateSelection("note-999");
        SettleBoard(h);
        Assert.NotNull(h.Board.Card("note-999"));
        Assert.Null(h.Board.ColumnCards("bucket-0"));
        Assert.InRange(h.Board.RealizedColumnCount, 1, 6);
        Assert.False(h.Wiring[oldCard]());
        Assert.False(h.Wiring[oldHeader]());
        oldComposer.Button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.False(oldComposer.Border.IsVisible);
        oldComposer.Box.Text = "Stale text";
        BoardComposerKey(oldComposer.Box, Avalonia.Input.Key.Enter);
        Assert.Empty(h.Added);
        h.Board.UpdateSelection("note-0");
        SettleBoard(h);
        Assert.NotNull(h.Board.Card("note-0"));
        Assert.True(h.Scroll.Offset.X < 300);
        Assert.NotSame(oldComposer.Box, h.Composer("bucket-0").Box);
    }

    [AvaloniaFact]
    public void ManyBucketBoardKeepsOpenDraftsAndFreshColumnDropContexts()
    {
        using var h = new BoardHarness();
        var project = ManyBucketProject();
        h.Render(project);
        SettleBoard(h);
        h.OpenComposer("bucket-0");
        var composer = h.Composer("bucket-0");
        composer.Box.Text = "Preserved draft";
        h.Board.UpdateSelection("note-999");
        SettleBoard(h);
        Assert.Same(composer.Box, h.Composer("bucket-0").Box);
        Assert.Equal("Preserved draft", composer.Box.Text);
        Assert.InRange(h.Board.RealizedColumnCount, 1, 7);
        project = project with { ChangeSequence = 2, Buckets = project.Buckets.Select(b => b.Id == "bucket-999"
            ? b with { Name = "Renamed distant bucket", Revision = 2 } : b).ToArray() };
        h.Render(project);
        SettleBoard(h);
        var wrapper = h.Board.ColumnCards("bucket-999")!.Parent!.Parent!.Parent!.Parent!;
        Assert.Same(h.Nodes["bucket-999"], wrapper.DataContext);
        Assert.Contains(ContentControls((Control)wrapper).OfType<TextBlock>(), t => t.Text == "Renamed distant bucket");
        h.Nodes["bucket-999"].IsDropTarget = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(Assert.IsType<Border>(Assert.IsType<Grid>(wrapper).Children[1]).IsVisible);
        BoardComposerKey(composer.Box, Avalonia.Input.Key.Escape);
        h.Scroll.Offset = new Vector(h.Scroll.Offset.X - 1, 0);
        SettleBoard(h);
        Assert.Null(h.Board.ColumnCards("bucket-0"));
        h.Board.Clear();
        Assert.Equal(0, h.Board.RealizedColumnCount);
    }

    [AvaloniaFact]
    public void ManyBucketBoardRestoresVerticalPositionAfterWholeColumnEviction()
    {
        using var h = new BoardHarness();
        h.Render(ManyBucketProject(64, 100));
        h.Board.UpdateSelection("note-50");
        SettleBoard(h);
        var offset = ViewportColumnScroll(h, "bucket-0").Offset.Y;
        Assert.True(offset > 320);
        h.Board.UpdateSelection("note-6399");
        SettleBoard(h);
        Assert.Null(h.Board.ColumnCards("bucket-0"));
        h.Scroll.Offset = new Vector(0, 0);
        SettleBoard(h);
        Assert.NotNull(h.Board.Card("note-50"));
        Assert.InRange(Math.Abs(ViewportColumnScroll(h, "bucket-0").Offset.Y - offset), 0, 64);
        Assert.InRange(h.Board.RealizedCardCount, 1, 100);
    }

    [AvaloniaFact]
    public void ManyBucketBoardKeepsEmptyColumnsNavigableAndHandlesMovesAndThresholdChanges()
    {
        using var h = new BoardHarness();
        var project = ManyBucketProject();
        project = project with { Slips = [project.Slips[0]] };
        h.Render(project);
        SettleBoard(h);
        Assert.Equal(1000 * 280 + 999 * 12, h.Panel.Bounds.Width);
        h.Scroll.Offset = new Vector(h.Scroll.Extent.Width, 0);
        SettleBoard(h);
        Assert.NotNull(h.Board.ColumnCards("bucket-999"));
        Assert.Empty(h.Board.RealizedColumnCards("bucket-999"));
        h.OpenComposer("bucket-999");
        Assert.True(h.Composer("bucket-999").Border.IsVisible);
        project = project with { ChangeSequence = 2, Slips = [project.Slips[0] with { BucketId = "bucket-999", Revision = 2 }] };
        h.Render(project);
        SettleBoard(h);
        Assert.NotNull(h.Board.Card("note-0"));
        h.Render(project with { ChangeSequence = 3, Buckets = [project.Buckets[999]] });
        SettleBoard(h);
        Assert.Equal(1, h.Board.RealizedColumnCount);
        Assert.NotNull(h.Board.Card("note-0"));
        Assert.Equal(0, h.Scroll.Offset.X);
    }

    [AvaloniaFact]
    public void ManyBucketBoardKeepsHorizontalAnchorOnBucketReorderAndOnlyLatestNavigationWins()
    {
        using var h = new BoardHarness();
        var project = ManyBucketProject();
        h.Render(project);
        h.Board.UpdateSelection("note-500");
        SettleBoard(h);
        var bucket = "bucket-499";
        var wrapper = (Control)h.Board.ColumnCards(bucket)!.Parent!.Parent!.Parent!.Parent!;
        var x = wrapper.TranslatePoint(default, h.Scroll)!.Value.X;
        project = project with { ChangeSequence = 2, Buckets = project.Buckets.Skip(1).Append(project.Buckets[0]).ToArray() };
        h.Render(project);
        SettleBoard(h);
        Assert.Same(wrapper, h.Board.ColumnCards(bucket)!.Parent!.Parent!.Parent!.Parent);
        Assert.InRange(Math.Abs(wrapper.TranslatePoint(default, h.Scroll)!.Value.X - x), 0, 1);
        h.Board.UpdateSelection("note-999");
        h.Board.UpdateSelection("note-1");
        SettleBoard(h);
        Assert.NotNull(h.Board.Card("note-1"));
        Assert.Same(Brushes.Purple, h.Board.Card("note-1")!.BorderBrush);
        Assert.Null(h.Board.Card("note-999"));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManyBucketReaderKeepsHeadingOnlyParentsAndNestedIndentation(bool grouped)
    {
        using var h = new ReaderHarness();
        var project = ManyBucketProject(128, grouped: grouped);
        project = project with
        {
            Buckets = project.Buckets.Select((b, i) => i % 2 == 1 ? b with { ParentBucketId = $"bucket-{i - 1}" } : b).ToArray(),
            Slips = project.Slips.Where((_, i) => i % 2 == 1).ToArray()
        };
        h.Render(project);
        SettleReader(h);
        Assert.Contains(h.Panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Bucket 0");
        Assert.Contains(h.Panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Bucket 1");
        Assert.DoesNotContain("note-0", h.Reader.Blocks.Keys);
        h.Reader.UpdateSelection("note-127");
        SettleReader(h);
        Assert.Contains("note-127", h.Reader.Blocks.Keys);
        Assert.Equal(grouped ? 14 : 28, h.Reader.Blocks["note-127"].Margin.Left);
        Assert.Contains(h.Panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Bucket 127");
        Assert.InRange(h.Reader.RealizedHeadingCount, 1, 30);
        h.Reader.UpdateSelection("note-1");
        h.Reader.UpdateSelection("note-127");
        SettleReader(h);
        Assert.Same(Brushes.Purple, h.Reader.Blocks["note-127"].BorderBrush);
        Assert.DoesNotContain("note-1", h.Reader.Blocks.Keys);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManyBucketViewportsRetirePictureLeasesAndRejectLateSectionLoads(bool board)
    {
        var pending = new TaskCompletionSource<ZetlPictureContent?>();
        using var pictures = new KastnPictureCache((_, slip) => slip.Id == "note-0"
            ? pending.Task : Task.FromResult<ZetlPictureContent?>(ReaderPictureContent() with
                { Sha256 = slip.Id, SlipId = slip.Id }), decodedBudget: 0);
        using var reader = new ReaderHarness(pictures: pictures);
        using var cards = new BoardHarness(pictures: pictures);
        var project = ManyBucketProject(100);
        project = project with { Slips = project.Slips.Select(s => s with
            { Type = ZetlSlipType.Picture, Picture = new() { Sha256 = s.Id, Width = 1, Height = 1 } }).ToArray() };
        if (board) { cards.Render(project); SettleBoard(cards); }
        else { reader.Render(project); SettleReader(reader); }
        var first = board ? cards.Board.Card("note-0")! : reader.Reader.Blocks["note-0"];
        var image = Assert.Single(ContentControls(first).OfType<Image>());
        var load = board ? BoardPictureLoad(cards.Board, "note-0") : ReaderPictureLoad(reader.Reader, "note-0");
        if (board) { cards.Board.UpdateSelection("note-99"); SettleBoard(cards); }
        else { reader.Reader.UpdateSelection("note-99"); SettleReader(reader); }
        pending.SetResult(ReaderPictureContent() with { Sha256 = "note-0", SlipId = "note-0" });
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(image.Source);
        IEnumerable<Image> Images() => board
            ? BoardRecords(cards.Board, "boardCards").Keys.Cast<string>().SelectMany(id => ContentControls(cards.Board.Card(id)!)).OfType<Image>()
            : reader.Reader.Blocks.Values.SelectMany(ContentControls).OfType<Image>();
        await SettlePictures(Images, board ? cards.Window : reader.Window);
        var liveImages = Images().ToArray();
        Assert.NotEmpty(liveImages);
        Assert.InRange(pictures.DecodedCount, 1, liveImages.Length + 1);
        reader.Reader.Clear();
        cards.Board.Clear();
        Assert.All(liveImages, current => Assert.Null(current.Source));
        await WaitForConditionAsync(() => pictures.DecodedResidentBytes == 0, "Retired sections should release every picture lease.");
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManyBucketReaderMovingPictureOutOfLiveSmallSectionReleasesItsLease(bool grouped)
    {
        using var pictures = PictureViewportCache();
        using var h = new ReaderHarness(pictures: pictures);
        var project = ManyBucketProject(100, 3, grouped);
        project = project with { Slips = project.Slips.Select(s => s.Id == "note-0" ? s with
            { Type = ZetlSlipType.Picture, Picture = new() { Sha256 = s.Id, Width = 1, Height = 1 } } : s).ToArray() };
        h.Render(project);
        SettleReader(h);
        await SettlePictures(() => h.Reader.Blocks.Values.SelectMany(ContentControls).OfType<Image>(), h.Window);
        var old = h.Reader.Blocks["note-0"];
        var image = Assert.Single(ContentControls(old).OfType<Image>());
        project = project with { ChangeSequence = 2, Slips = project.Slips.Select(s => s.Id == "note-0"
            ? s with { BucketId = "bucket-99", Revision = 2 } : s).ToArray() };
        h.Render(project);
        SettleReader(h);
        Assert.DoesNotContain("note-0", h.Reader.Blocks.Keys);
        Assert.Null(image.Source);
        Assert.Equal(0, pictures.DecodedResidentBytes);
        PressContent(old);
        Assert.Empty(h.Selected);
        h.Reader.UpdateSelection("note-0");
        SettleReader(h);
        Assert.Contains("note-0", h.Reader.Blocks.Keys);
        await SettlePictures(() => h.Reader.Blocks.Values.SelectMany(ContentControls).OfType<Image>(), h.Window);
        Assert.Contains("note-0", h.Reader.Blocks.Keys);
        Assert.NotSame(old, h.Reader.Blocks["note-0"]);
    }
}
