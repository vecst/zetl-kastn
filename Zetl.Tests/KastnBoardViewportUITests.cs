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
    private static void SettleBoard(BoardHarness h)
    {
        h.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        h.Window.UpdateLayout();
    }
    private static ScrollViewer ViewportColumnScroll(BoardHarness h, string id) =>
        h.Board.ColumnCards(id)!.GetVisualAncestors().OfType<ScrollViewer>().First();

    [AvaloniaFact]
    public void BoardViewportBoundsCardsNavigatesToEndAndRetiresOldActions()
    {
        using var h = new BoardHarness();
        var project = ViewportProject();
        h.Render(project);
        SettleBoard(h);
        Assert.InRange(h.Board.RealizedCardCount, 1, 50);
        var old = h.Board.Card("note-0")!;
        Assert.NotNull(old);
        Assert.Null(h.Board.Card("note-999"));
        h.Board.UpdateSelection("note-999");
        SettleBoard(h);
        Assert.NotNull(h.Board.Card("note-999"));
        Assert.Same(Brushes.Purple, h.Board.Card("note-999")!.BorderBrush);
        Assert.Null(h.Board.Card("note-0"));
        Assert.InRange(h.Board.RealizedCardCount, 1, 50);
        Assert.False(h.Wiring[old]());
        PressContent(old);
        Assert.Empty(h.Selected);
        Assert.True(ViewportColumnScroll(h, "render-bucket").Offset.Y > 320);
        h.Board.UpdateSelection("note-0");
        SettleBoard(h);
        Assert.NotNull(h.Board.Card("note-0"));
        Assert.True(ViewportColumnScroll(h, "render-bucket").Offset.Y < 320);
    }

    [AvaloniaFact]
    public void BoardViewportKeepsScrollDraftAndFreshContentAcrossEditsAndModeChanges()
    {
        using var h = new BoardHarness();
        var project = ViewportProject();
        h.Render(project);
        SettleBoard(h);
        h.Board.UpdateSelection("note-500");
        SettleBoard(h);
        h.OpenComposer("render-bucket");
        var composer = h.Composer("render-bucket");
        composer.Box.Text = "Unfinished draft";
        var offset = ViewportColumnScroll(h, "render-bucket").Offset;
        var card = h.Board.Card("note-500");
        h.Render(project with { ChangeSequence = 2, Slips = project.Slips.Select(s => s.Id == "note-999"
            ? s with { Revision = 2, Text = "Edited offscreen" } : s).ToArray() }, selected: "note-500");
        SettleBoard(h);
        Assert.Same(card, h.Board.Card("note-500"));
        Assert.Equal(offset, ViewportColumnScroll(h, "render-bucket").Offset);
        Assert.Equal("Unfinished draft", h.Composer("render-bucket").Box.Text);
        h.Board.Suspend();
        h.Render(project with { ChangeSequence = 3 });
        SettleBoard(h);
        Assert.Same(composer.Box, h.Composer("render-bucket").Box);
        Assert.Equal("Unfinished draft", composer.Box.Text);
        h.Render(project with { ChangeSequence = 4 }, visible: project.Slips.Take(2).ToArray());
        SettleBoard(h);
        Assert.Equal(2, h.Board.RealizedCardCount);
        Assert.Equal(0, ViewportColumnScroll(h, "render-bucket").Offset.Y);
        h.Render(project with { Id = "another" });
        SettleBoard(h);
        Assert.Equal("", h.Composer("render-bucket").Box.Text ?? "");
        h.Board.Clear();
        SettleBoard(h);
        Assert.Equal(0, h.Board.RealizedCardCount);
        Assert.Empty(h.Panel.Children);
    }

    [AvaloniaFact]
    public void BoardViewportHandlesOffscreenColumnsCardMovesAndLatestSelection()
    {
        using var h = new BoardHarness();
        var project = ViewportProject();
        project = project with
        {
            Buckets = Enumerable.Range(0, 10).Select(i => project.Buckets[0] with { Id = $"bucket-{i}", Name = $"Bucket {i}" }).ToArray(),
            Slips = project.Slips.Select((s, i) => s with { BucketId = $"bucket-{i / 100}" }).ToArray()
        };
        h.Render(project);
        SettleBoard(h);
        Assert.InRange(h.Board.RealizedCardCount, 1, 80);
        h.Board.UpdateSelection("note-999");
        SettleBoard(h);
        Assert.NotNull(h.Board.Card("note-999"));
        Assert.True(h.Scroll.Offset.X > 600);
        project = project with { ChangeSequence = 2, Slips = project.Slips.Select(s => s.Id == "note-999"
            ? s with { Revision = 2, BucketId = "bucket-0" } : s).ToArray() };
        h.Render(project);
        h.Board.UpdateSelection("note-999");
        h.Board.UpdateSelection("note-0");
        SettleBoard(h);
        Assert.NotNull(h.Board.Card("note-0"));
        Assert.Same(Brushes.Purple, h.Board.Card("note-0")!.BorderBrush);
        Assert.True(h.Scroll.Offset.X < 300);
        Assert.InRange(h.Board.RealizedCardCount, 1, 80);
    }

    [AvaloniaFact]
    public void BoardViewportReleasesDistantColumnsAndRestoresTheirPositionAndDraft()
    {
        using var h = new BoardHarness();
        var project = ViewportProject();
        project = project with
        {
            Buckets = Enumerable.Range(0, 10).Select(i => project.Buckets[0] with { Id = $"bucket-{i}" }).ToArray(),
            Slips = project.Slips.Select((s, i) => s with { BucketId = $"bucket-{i / 100}" }).ToArray()
        };
        h.Render(project);
        SettleBoard(h);
        Assert.Null(h.Board.Card("note-900"));
        h.Board.UpdateSelection("note-50");
        SettleBoard(h);
        var column = ViewportColumnScroll(h, "bucket-0");
        var offset = column.Offset.Y;
        h.OpenComposer("bucket-0");
        var composer = h.Composer("bucket-0");
        composer.Box.Text = "Keep this draft";
        SettleBoard(h);
        h.Board.UpdateSelection("note-999");
        SettleBoard(h);
        Assert.Null(h.Board.Card("note-50"));
        Assert.NotNull(h.Board.Card("note-999"));
        Assert.Same(composer.Box, h.Composer("bucket-0").Box);
        Assert.Equal("Keep this draft", composer.Box.Text);
        h.Scroll.Offset = new Vector(0, 0);
        SettleBoard(h);
        Assert.NotNull(h.Board.Card("note-50"));
        Assert.InRange(Math.Abs(column.Offset.Y - offset), 0, 64);
        Assert.Null(h.Board.Card("note-999"));
        Assert.InRange(h.Board.RealizedCardCount, 1, 80);
        h.Board.Suspend();
        h.Scroll.Offset = new Vector(2000, 0);
        h.Board.Clear();
        SettleBoard(h);
        Assert.Equal(0, h.Board.RealizedCardCount);
    }

    [AvaloniaFact]
    public void BoardViewportPreservesLogicalAnchorOnReorderAndPicturePeekOnEviction()
    {
        using var h = new BoardHarness(fetch: (_, slip) => Task.FromResult<ZetlPictureContent?>(
            ReaderPictureContent() with { SlipId = slip.Id }));
        var project = ViewportProject();
        project = project with { Slips = project.Slips.Select(s => s.Id == "note-500"
            ? s with { Picture = new() { Sha256 = "picture", Width = 1, Height = 1 } } : s).ToArray() };
        h.Render(project);
        h.Board.UpdateSelection("note-500");
        SettleBoard(h);
        var card = h.Board.Card("note-500")!;
        var images = ContentControls(card).OfType<Image>().ToArray();
        Assert.Equal(2, images.Length);
        PressContent(images[0]);
        SettleBoard(h);
        Assert.True(images[1].IsVisible);
        card = h.Board.Card("note-500")!;
        var y = card.TranslatePoint(default, ViewportColumnScroll(h, "render-bucket"))!.Value.Y;
        // Remove a row before the viewport without changing the selected card.
        project = project with { ChangeSequence = 2, Slips = project.Slips.Skip(1).ToArray() };
        h.Render(project, selected: "note-500");
        SettleBoard(h);
        Assert.NotNull(h.Board.Card("note-500"));
        Assert.InRange(Math.Abs(h.Board.Card("note-500")!.TranslatePoint(default,
            ViewportColumnScroll(h, "render-bucket"))!.Value.Y - y), 0, 1);
        h.Board.UpdateSelection("note-999");
        SettleBoard(h);
        Assert.Null(h.Board.Card("note-500"));
        h.Board.UpdateSelection("note-500");
        SettleBoard(h);
        Assert.True(ContentControls(h.Board.Card("note-500")!).OfType<Image>().Last().IsVisible);
    }

    [AvaloniaFact]
    public async Task BoardViewportDefersPicturesAndDropsAssignmentsAfterEviction()
    {
        var replies = new Dictionary<string, TaskCompletionSource<ZetlPictureContent?>>();
        using var h = new BoardHarness(fetch: (_, slip) => { replies.Add(slip.Id, new()); return replies[slip.Id].Task; });
        var project = ViewportProject(300);
        project = project with { Slips = project.Slips.Select(s => s.Id is "note-0" or "note-299"
            ? s with { Type = ZetlSlipType.Picture, Picture = new() { Sha256 = s.Id, Width = 1, Height = 1 } } : s).ToArray() };
        h.Render(project);
        SettleBoard(h);
        Assert.Single(replies);
        var first = h.Board.Card("note-0")!;
        var image = Assert.Single(ContentControls(first).OfType<Image>());
        var load = BoardPictureLoad(h.Board, "note-0");
        h.Board.UpdateSelection("note-299");
        SettleBoard(h);
        Assert.Equal(2, replies.Count);
        replies["note-0"].SetResult(ReaderPictureContent() with { SlipId = "note-0", Sha256 = "note-0" });
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(image.Source);
        var lastLoad = BoardPictureLoad(h.Board, "note-299");
        h.Board.Clear();
        replies["note-299"].SetResult(null);
        await lastLoad.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, h.Board.RealizedCardCount);
    }
}
