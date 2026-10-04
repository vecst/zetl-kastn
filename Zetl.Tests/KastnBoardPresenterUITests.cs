using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaFact]
    public void BoardPresenterReusesColumnsCardsAndComposerAcrossReorderAndMoves()
    {
        using var h = new BoardHarness();
        var project = BoardPresenterProject();
        h.Render(project);
        var column = h.Panel.Children[0];
        var otherColumn = h.Panel.Children[1];
        var first = h.Board.Card("render-one")!;
        var second = h.Board.Card("render-two")!;
        var composer = h.Composer("render-bucket");
        h.OpenComposer("render-bucket");
        composer.Box.Text = "unfinished card";
        h.Render(project, selected: "render-two");
        Assert.Same(first, h.Board.Card("render-one"));
        Assert.Same(Brushes.Purple, second.BorderBrush);
        Assert.Same(Brushes.Gray, first.BorderBrush);
        var oldContext = column.DataContext;
        project = project with
        {
            ChangeSequence = 2, Buckets = [project.Buckets[1], project.Buckets[0] with { Name = "Renamed", Revision = 2 }],
            Slips = [project.Slips[1] with { BucketId = "other-bucket" }, project.Slips[0]]
        };
        h.Render(project);
        Assert.Same(otherColumn, h.Panel.Children[0]);
        Assert.Same(column, h.Panel.Children[1]);
        Assert.NotSame(oldContext, column.DataContext);
        Assert.Same(h.Nodes["render-bucket"], column.DataContext);
        Assert.Same(h.Nodes["render-two"], second.DataContext);
        Assert.Same(first, h.Board.Card("render-one"));
        Assert.Same(second, h.Board.Card("render-two"));
        Assert.Same(second.Parent, Assert.Single(h.Board.ColumnCards("other-bucket")!.Children));
        Assert.Contains(ContentControls(column).OfType<TextBlock>(), text => text.Text == "Renamed");
        Assert.Contains(ContentControls(column).OfType<TextBlock>(), text => text.Text == "(1)");
        Assert.Same(composer.Box, h.Composer("render-bucket").Box);
        Assert.Equal("unfinished card", composer.Box.Text);
        Assert.True(composer.Border.IsVisible);
    }

    [AvaloniaFact]
    public void BoardPresenterInvalidatesOnlyCardsWhosePresentationChanged()
    {
        using var h = new BoardHarness();
        var project = BoardPresenterProject();
        h.Render(project);
        var first = h.Board.Card("render-one");
        var second = h.Board.Card("render-two");
        project = project with { ChangeSequence = 2, Slips = [project.Slips[0] with { Revision = 2, Text = "edited" }, project.Slips[1]] };
        h.Render(project);
        Assert.NotSame(first, h.Board.Card("render-one"));
        Assert.Same(second, h.Board.Card("render-two"));
        var changed = h.Board.Card("render-one");
        project = project with { ChangeSequence = 3,
            Buckets = [project.Buckets[0] with { RenderKind = ZetlBlockKinds.Task }, project.Buckets[1]] };
        h.Render(project);
        Assert.NotSame(changed, h.Board.Card("render-one"));
        Assert.NotSame(second, h.Board.Card("render-two"));
        changed = h.Board.Card("render-one");
        h.Render(project, preferSlipKind: true);
        Assert.NotSame(changed, h.Board.Card("render-one"));
        changed = h.Board.Card("render-one");
        h.Render(project, preferSlipKind: true, untitled: "Untitled");
        Assert.NotSame(changed, h.Board.Card("render-one"));
    }

    [AvaloniaFact]
    public void BoardPresenterKeepsHorizontalAndColumnScrollAndClampsShorterContent()
    {
        using var h = new BoardHarness();
        var original = BoardPresenterProject();
        var project = original with
        {
            Buckets = Enumerable.Range(0, 8).Select(i => original.Buckets[0] with { Id = $"b-{i}" }).ToArray(),
            Slips = Enumerable.Range(0, 70).Select(i => original.Slips[0] with { Id = $"s-{i}", BucketId = "b-0", Text = $"Card {i}" }).ToArray()
        };
        h.Render(project);
        var columnScroll = Assert.IsType<ScrollViewer>(h.Board.ColumnCards("b-0")!.Parent);
        h.Scroll.Offset = new Vector(300, 0);
        columnScroll.Offset = new Vector(0, 180);
        Assert.Equal(300, h.Scroll.Offset.X);
        Assert.Equal(180, columnScroll.Offset.Y);
        h.Render(project with { ChangeSequence = 2, Slips = project.Slips.Select(slip => slip.Id == "s-0" ? slip with { Revision = 2, Text = "edited" } : slip).ToArray() });
        Assert.Equal(300, h.Scroll.Offset.X);
        Assert.Equal(180, columnScroll.Offset.Y);
        h.Render(project with { ChangeSequence = 3, Buckets = [project.Buckets[0]], Slips = [project.Slips[0]] });
        Assert.Equal(Vector.Zero, h.Scroll.Offset);
        Assert.Equal(Vector.Zero, columnScroll.Offset);
        h.Render(project with { Id = "another" });
        Assert.Equal(Vector.Zero, h.Scroll.Offset);
    }

    [AvaloniaTheory]
    [InlineData("replace")]
    [InlineData("filter")]
    [InlineData("clear")]
    [InlineData("suspend")]
    [InlineData("project")]
    [InlineData("dispose")]
    public void BoardPresenterRetiresCardActionsAndDragWiring(string change)
    {
        using var h = new BoardHarness();
        var project = BoardPresenterProject();
        project = project with { Slips = [project.Slips[0] with { BlockKind = ZetlBlockKinds.Task, Picture = ReaderPictureProject().Slips[0].Picture }, project.Slips[1]] };
        h.Render(project);
        var card = h.Board.Card("render-one")!;
        var checkbox = ContentControls(card).OfType<TextBlock>().Single(text => text.Text == "☐");
        var thumbnail = ContentControls(card).OfType<Image>().First();
        var expanded = ContentControls(card).OfType<Image>().Last();
        switch (change)
        {
            case "replace": h.Render(project with { ChangeSequence = 2, Slips = [project.Slips[0] with { Revision = 2 }, project.Slips[1]] }); break;
            case "filter": h.Render(project, visible: [project.Slips[1]]); break;
            case "project": h.Render(project with { Id = "new" }); break;
            case "clear": h.Board.Clear(); break;
            case "dispose": h.Board.Dispose(); break;
            default: h.Board.Suspend(); break;
        }
        Assert.False(h.Wiring[card]());
        PressContent(card);
        BoardDoubleTap(card);
        Assert.True(PressContent(checkbox).Handled);
        PressContent(thumbnail);
        Assert.False(expanded.IsVisible);
        Assert.Empty(h.Selected);
        Assert.Empty(h.Edited);
        Assert.Empty(h.Checked);
        if (change == "dispose")
        {
            h.Render(project);
            Assert.Empty(h.Panel.Children);
            Assert.Null(h.Board.Card("render-one"));
        }
        else
        {
            h.Render(project);
            var live = h.Board.Card("render-one")!;
            PressContent(live);
            BoardDoubleTap(live);
            Assert.Equal("render-one", Assert.Single(h.Selected));
            Assert.Equal("render-one", Assert.Single(h.Edited));
        }
    }

    [AvaloniaTheory]
    [InlineData("latest")]
    [InlineData("replace")]
    [InlineData("clear")]
    [InlineData("suspend")]
    [InlineData("dispose")]
    public void BoardPresenterQueuedScrollingChecksSelectionAndLifetime(string change)
    {
        using var h = new BoardHarness();
        var project = BoardPresenterProject();
        h.Render(project);
        var first = h.Board.Card("render-one")!;
        var second = h.Board.Card("render-two")!;
        var firstRequests = 0;
        var secondRequests = 0;
        first.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => firstRequests++);
        second.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => secondRequests++);
        h.Board.UpdateSelection("render-one");
        Assert.Equal(1, firstRequests);
        switch (change)
        {
            case "latest": h.Board.UpdateSelection("render-two"); break;
            case "replace": h.Render(project with { ChangeSequence = 2, Slips = [project.Slips[0] with { Revision = 2 }, project.Slips[1]] }); break;
            case "clear": h.Board.Clear(); break;
            case "dispose": h.Board.Dispose(); break;
            default: h.Board.Suspend(); break;
        }
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, firstRequests);
        Assert.Equal(change == "latest" ? 2 : 0, secondRequests);
    }

    [AvaloniaFact]
    public async Task BoardPresenterPendingDualPictureSurvivesMoveAndKeepsPeekAcrossRevision()
    {
        var reply = new TaskCompletionSource<ZetlPictureContent?>();
        var requests = 0;
        using var h = new BoardHarness(fetch: (id, _) => { Assert.Equal("board", id); requests++; return reply.Task; });
        var project = BoardPresenterProject();
        project = project with { Slips = [project.Slips[0] with { Picture = ReaderPictureProject().Slips[0].Picture }, project.Slips[1]] };
        h.Render(project);
        var card = h.Board.Card("render-one")!;
        var images = ContentControls(card).OfType<Image>().ToArray();
        PressContent(images[0]);
        Assert.True(images[1].IsVisible);
        Assert.Empty(h.Selected);
        var load = BoardPictureLoad(h.Board, "render-one");
        project = project with { ChangeSequence = 2, Slips = [project.Slips[0] with { BucketId = "other-bucket" }, project.Slips[1] with { Revision = 2 }] };
        h.Render(project);
        Assert.Same(card, h.Board.Card("render-one"));
        reply.SetResult(ReaderPictureContent());
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        var bitmap = h.Pictures.FindDecoded("asset", 260);
        Assert.NotNull(bitmap);
        Assert.All(images, image => Assert.Same(bitmap, image.Source));
        Assert.Equal(1, requests);
        h.Render(project with { ChangeSequence = 3, Slips = [project.Slips[0] with { Revision = 2, Text = "new caption" }, project.Slips[1]] });
        Assert.True(ContentControls(h.Board.Card("render-one")!).OfType<Image>().Last().IsVisible);
        Assert.Equal(1, requests);
        h.Render(project, visible: [project.Slips[1]]);
        h.Render(project);
        Assert.False(ContentControls(h.Board.Card("render-one")!).OfType<Image>().Last().IsVisible);
    }

    [AvaloniaTheory]
    [InlineData("replace")]
    [InlineData("filter")]
    [InlineData("clear")]
    [InlineData("project")]
    [InlineData("dispose")]
    public async Task BoardPresenterDelayedPictureNeverAssignsToRetiredCard(string change)
    {
        var reply = new TaskCompletionSource<ZetlPictureContent?>();
        using var h = new BoardHarness(fetch: (_, _) => reply.Task);
        var project = BoardPresenterProject();
        project = project with { Slips = [project.Slips[0] with { Type = ZetlSlipType.Picture, Picture = ReaderPictureProject().Slips[0].Picture }, project.Slips[1]] };
        h.Render(project);
        var old = h.Board.Card("render-one")!;
        var load = BoardPictureLoad(h.Board, "render-one");
        switch (change)
        {
            case "replace": h.Render(project with { ChangeSequence = 2, Slips = [project.Slips[0] with { Revision = 2 }, project.Slips[1]] }); break;
            case "filter": h.Render(project, visible: [project.Slips[1]]); break;
            case "project": h.Render(project with { Id = "other", Slips = [project.Slips[1]] }); break;
            case "dispose": h.Board.Dispose(); break;
            default: h.Board.Clear(); break;
        }
        var nextLoad = change == "replace" ? BoardPictureLoad(h.Board, "render-one") : null;
        reply.SetResult(ReaderPictureContent());
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        if (nextLoad is not null)
        {
            await nextLoad.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(Assert.Single(ContentControls(h.Board.Card("render-one")!).OfType<Image>()).Source);
        }
        Assert.Null(Assert.Single(ContentControls(old).OfType<Image>()).Source);
        if (change != "replace") Assert.Null(h.Pictures.FindDecoded("asset", 260));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoardPresenterPictureFailureIsVisibleForPictureAndDualCards(bool dual)
    {
        using var h = new BoardHarness(fetch: (_, _) => Task.FromException<ZetlPictureContent?>(new IOException("offline")));
        var project = BoardPresenterProject();
        project = project with { Slips = [project.Slips[0] with { Type = dual ? ZetlSlipType.Text : ZetlSlipType.Picture, Picture = ReaderPictureProject().Slips[0].Picture }, project.Slips[1]] };
        h.Render(project);
        await BoardPictureLoad(h.Board, "render-one");
        var status = ContentControls(h.Board.Card("render-one")!).OfType<TextBlock>().Single(text => text.Text == "Picture unavailable.");
        Assert.True(status.IsVisible);
    }

    [AvaloniaTheory]
    [InlineData("unchanged")]
    [InlineData("typing")]
    [InlineData("discard-reopen")]
    [InlineData("project")]
    [InlineData("remove")]
    [InlineData("dispose")]
    public async Task BoardComposerCompletionPreservesNewerDraftAndRetiredControls(string change)
    {
        var reply = new TaskCompletionSource<bool>();
        using var h = new BoardHarness(add: (_, _) => reply.Task);
        var project = BoardPresenterProject();
        h.Render(project);
        h.OpenComposer("render-bucket");
        var composer = h.Composer("render-bucket");
        composer.Box.Text = " submitted ";
        var commit = h.Commit("render-bucket", keepOpen: true);
        Assert.Equal(("render-bucket", "submitted"), Assert.Single(h.Added));
        Assert.False(await h.Commit("render-bucket", keepOpen: true));
        switch (change)
        {
            case "typing": composer.Box.Text = "new writing"; break;
            case "discard-reopen":
                BoardComposerKey(composer.Box, Key.Escape);
                h.OpenComposer("render-bucket");
                composer.Box.Text = " submitted ";
                break;
            case "project": h.Render(project with { Id = "other" }); break;
            case "remove": h.Render(project with { ChangeSequence = 2, Buckets = [project.Buckets[1]], Slips = [] }); break;
            case "dispose": h.Board.Dispose(); break;
        }
        h.Render(change is "project" or "remove" or "dispose" ? h.Project! : project with { ChangeSequence = 3 });
        reply.SetResult(true);
        Assert.True(await commit.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(change switch { "unchanged" => "", "typing" => "new writing", _ => " submitted " }, composer.Box.Text);
        Assert.Single(h.Added);
        if (change == "project") Assert.Equal("", h.Composer("render-bucket").Box.Text ?? "");
    }

    [AvaloniaFact]
    public async Task BoardComposerEnterShiftEnterFocusLossFailureAndEscapeKeepTheirSemantics()
    {
        var success = false;
        using var h = new BoardHarness(add: (_, _) => Task.FromResult(success));
        h.Render(BoardPresenterProject());
        h.OpenComposer("render-bucket");
        var composer = h.Composer("render-bucket");
        composer.Box.Text = "draft";
        var shiftEnter = BoardComposerKey(composer.Box, Key.Enter, KeyModifiers.Shift);
        Assert.False(shiftEnter.Handled);
        Assert.Empty(h.Added);
        Assert.False(await h.Commit("render-bucket", keepOpen: false));
        Assert.True(composer.Border.IsVisible);
        Assert.Equal("draft", composer.Box.Text);
        success = true;
        Assert.True(BoardComposerKey(composer.Box, Key.Enter).Handled);
        Assert.Equal("", composer.Box.Text);
        Assert.True(composer.Border.IsVisible);
        composer.Box.Text = "focus commit";
        composer.Box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Assert.False(composer.Border.IsVisible);
        Assert.Equal("", composer.Box.Text);
        h.OpenComposer("render-bucket");
        composer.Box.Text = "discard";
        Assert.True(BoardComposerKey(composer.Box, Key.Escape).Handled);
        Assert.False(composer.Border.IsVisible);
        Assert.Equal("", composer.Box.Text);
        Assert.Equal(3, h.Added.Count);
    }

    [AvaloniaTheory]
    [InlineData("clear")]
    [InlineData("suspend")]
    [InlineData("project")]
    [InlineData("remove")]
    [InlineData("dispose")]
    public void RetiredBoardColumnsCannotOpenOrSubmitComposers(string change)
    {
        using var h = new BoardHarness();
        var project = BoardPresenterProject();
        h.Render(project);
        var composer = h.Composer("render-bucket");
        switch (change)
        {
            case "clear": h.Board.Clear(); break;
            case "project": h.Render(project with { Id = "other" }); break;
            case "remove": h.Render(project with { ChangeSequence = 2, Buckets = [project.Buckets[1]], Slips = [] }); break;
            case "dispose": h.Board.Dispose(); break;
            default: h.Board.Suspend(); break;
        }
        composer.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(composer.Border.IsVisible);
        composer.Box.Text = "stale draft";
        BoardComposerKey(composer.Box, Key.Enter);
        composer.Box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Assert.Empty(h.Added);
        Assert.Equal("stale draft", composer.Box.Text);
    }

    [AvaloniaFact]
    public void BoardPresenterExcludesDeletedBucketsButKeepsEmptyColumnsAndDropContext()
    {
        using var h = new BoardHarness();
        var project = BoardPresenterProject();
        var deleted = project.Buckets[0] with { Id = "deleted", Name = "Deleted", Settings = new() { Kind = "Deleted" } };
        project = project with { Buckets = [project.Buckets[0], project.Buckets[1], deleted],
            Slips = [project.Slips[0], project.Slips[1] with { BucketId = deleted.Id }] };
        h.Render(project);
        Assert.Equal(2, h.Panel.Children.Count);
        Assert.Empty(h.Board.ColumnCards("other-bucket")!.Children);
        Assert.Null(h.Board.ColumnCards("deleted"));
        Assert.Null(h.Board.Card("render-two"));
        h.Nodes["render-bucket"].IsDropTarget = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(Assert.IsType<Border>(Assert.IsType<Grid>(h.Panel.Children[0]).Children[1]).IsVisible);
        h.Render(project with { ChangeSequence = 2 });
        Assert.False(Assert.IsType<Border>(Assert.IsType<Grid>(h.Panel.Children[0]).Children[1]).IsVisible);
    }

    [AvaloniaFact]
    public void WindowBoardKeepsComposerAcrossModeToggleAndDisposesOnClose()
    {
        var (window, _, _) = WorkflowWindow();
        var board = WindowField<KastnBoardPresenter>(window, "boardPresenter");
        try
        {
            window.viewModeBoardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var card = board.Card("render-one");
            var composer = BoardComposerControls(board.ColumnCards("render-bucket")!);
            composer.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            composer.Box.Text = "unfinished";
            window.viewModeListButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.viewModeBoardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(card, board.Card("render-one"));
            Assert.Same(composer.Box, BoardComposerControls(board.ColumnCards("render-bucket")!).Box);
            Assert.Equal("unfinished", composer.Box.Text);
        }
        finally { CloseWindow(window); }
        Assert.True(board.IsDisposed);
        Assert.Null(board.Card("render-one"));
        Assert.Empty(window.boardColumnsPanel.Children);
    }

    private static ZetlProjectSnapshot BoardPresenterProject()
    {
        var project = RenderProject("board", "one");
        return project with { Buckets = [project.Buckets[0], project.Buckets[0] with { Id = "other-bucket", Name = "Other" }] };
    }
    private static void BoardDoubleTap(Control control) => control.RaiseEvent(new TappedEventArgs(
        InputElement.DoubleTappedEvent, new PointerPressedEventArgs(control,
            new Avalonia.Input.Pointer(0, PointerType.Mouse, true), control, new Point(), 0,
            new PointerPointProperties(), KeyModifiers.None)));
    private static KeyEventArgs BoardComposerKey(TextBox box, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
        box.RaiseEvent(args);
        return args;
    }
    private static (Border Border, TextBox Box, Button Button) BoardComposerControls(StackPanel cards)
    {
        var grid = Assert.IsType<Grid>(Assert.IsType<ScrollViewer>(cards.Parent).Parent);
        var composer = Assert.IsType<Border>(grid.Children[2]);
        return (composer, Assert.IsType<TextBox>(composer.Child), Assert.IsType<Button>(Assert.IsType<Grid>(grid.Children[0]).Children[2]));
    }
    private static IDictionary BoardRecords(KastnBoardPresenter board, string name) =>
        (IDictionary)typeof(KastnBoardPresenter).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(board)!;
    private static Task BoardPictureLoad(KastnBoardPresenter board, string id)
    {
        var card = BoardRecords(board, "boardCards")[id]!;
        return (Task)card.GetType().GetProperty("PictureLoad")!.GetValue(card)!;
    }
    private sealed class BoardHarness : IDisposable
    {
        public readonly StackPanel Panel = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        public readonly ScrollViewer Scroll;
        public readonly Window Window;
        public readonly KastnPictureCache Pictures;
        public readonly KastnBoardPresenter Board;
        public readonly List<string> Selected = [];
        public readonly List<string> Edited = [];
        public readonly List<string> Checked = [];
        public readonly List<(string Bucket, string Text)> Added = [];
        public readonly Dictionary<Control, Func<bool>> Wiring = [];
        public Dictionary<string, KastnTreeNode> Nodes = [];
        public ZetlProjectSnapshot? Project;
        private readonly Func<string, string, Task<bool>> add;
        private readonly bool ownsPictures;
        public BoardHarness(Func<string, string, Task<bool>>? add = null,
            Func<string, ZetlSlipSnapshot, Task<ZetlPictureContent?>>? fetch = null,
            KastnPictureCache? pictures = null)
        {
            this.add = (id, text) => { Added.Add((id, text)); return add?.Invoke(id, text) ?? Task.FromResult(false); };
            Scroll = new() { Content = Panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Window = new() { Width = 600, Height = 320, Content = Scroll };
            ownsPictures = pictures is null;
            Pictures = pictures ?? new(fetch ?? ((_, _) => Task.FromResult<ZetlPictureContent?>(null)));
            Board = new(Panel, Scroll, Pictures);
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }
        public void Render(ZetlProjectSnapshot project, IReadOnlyList<ZetlSlipSnapshot>? visible = null,
            string? selected = null, bool preferSlipKind = false, string untitled = "Untitled slip")
        {
            Project = project;
            Nodes = project.Buckets.Select(bucket => new KastnTreeNode { Kind = KastnTreeNodeKind.Bucket, Id = bucket.Id, Label = bucket.Name, Bucket = bucket })
                .Concat(project.Slips.Select(slip => new KastnTreeNode { Kind = KastnTreeNodeKind.Slip, Id = slip.Id, Label = slip.Text, Slip = slip }))
                .ToDictionary(node => node.Id);
            var index = new KastnProjectIndex(project);
            var slips = visible ?? project.Slips;
            var key = KastnViewRenderKey.Create(project, slips, ZetlViewDefaults.CreateAll()[0],
                new ZetlAppSettings { KastnPreferSlipKindOverBucketKind = preferSlipKind, UntitledSlipTitle = untitled });
            var renderer = new KastnSlipContentRenderer(index, ContentResources, Selected.Add,
                id => { Checked.Add(id); return Task.CompletedTask; });
            Board.Render(new(index, slips, key, false, renderer, new(Brushes.Gray, Brushes.Purple, Brushes.Beige, Brushes.White),
                id => Nodes.GetValueOrDefault(id), new(Selected.Add, id => { Edited.Add(id); return Task.CompletedTask; }, add,
                    (card, guard) => Wiring[card] = guard, (header, border, guard) => { Wiring[header] = guard; Wiring[border] = guard; })), selected);
            Window.UpdateLayout();
        }
        public (Border Border, TextBox Box, Button Button) Composer(string id) => BoardComposerControls(Board.ColumnCards(id)!);
        public void OpenComposer(string id) => Composer(id).Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        public Task<bool> Commit(string id, bool keepOpen)
        {
            var column = BoardRecords(Board, "boardColumns")[id]!;
            return (Task<bool>)typeof(KastnBoardPresenter).GetMethod("CommitBoardComposerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Board, [id, column, add, keepOpen])!;
        }
        public void Dispose()
        {
            Board.Dispose();
            if (ownsPictures) Pictures.Dispose();
            Window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
