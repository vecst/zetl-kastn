using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaFact]
    public void ReaderPresenterReusesControlsAcrossSelectionReorderAndGroupChanges()
    {
        using var h = new ReaderHarness();
        var project = RenderProject("reader", "one");
        h.Render(project);
        var first = h.Reader.Blocks["render-one"];
        var second = h.Reader.Blocks["render-two"];
        var heading = h.Panel.Children[0];
        h.Render(project, selected: "render-two");
        Assert.Same(first, h.Reader.Blocks["render-one"]);
        Assert.Same(second, h.Reader.Blocks["render-two"]);
        Assert.Same(heading, h.Panel.Children[0]);
        Assert.Same(Brushes.Purple, second.BorderBrush);
        Assert.Same(Brushes.Transparent, first.BorderBrush);
        h.Render(project with { ChangeSequence = 2, Slips = [project.Slips[1], project.Slips[0]] });
        Assert.Same(second, h.Panel.Children[1]);
        Assert.Same(first, h.Panel.Children[2]);
        var grouped = project with { ChangeSequence = 3, Buckets = [project.Buckets[0] with { Revision = 2, RenderKind = "group" }] };
        h.Render(grouped);
        var box = Assert.IsType<Border>(Assert.Single(h.Panel.Children));
        var content = Assert.IsType<StackPanel>(box.Child);
        Assert.Same(first, content.Children[1]);
        Assert.Same(second, content.Children[2]);
        h.Render(grouped with { ChangeSequence = 4, Buckets = [grouped.Buckets[0] with { Name = "Renamed", Revision = 3 }] });
        Assert.Same(box, Assert.Single(h.Panel.Children));
        Assert.Equal("Renamed", Assert.IsType<TextBlock>(content.Children[0]).Text);
        h.Render(project with { ChangeSequence = 5 });
        Assert.Same(first, h.Panel.Children[1]);
        Assert.Same(second, h.Panel.Children[2]);
        Assert.DoesNotContain(first, content.Children);
    }

    [AvaloniaFact]
    public void ReaderPresenterKeepsScrollClampsShorterContentAndResetsForAnotherProject()
    {
        using var h = new ReaderHarness();
        var original = RenderProject("reader", "one");
        var project = original with { Slips = Enumerable.Range(0, 80).Select(i => original.Slips[0] with { Id = $"slip-{i}", Text = $"Note {i}" }).ToArray() };
        h.Render(project);
        h.Scroll.Offset = new Vector(0, 220);
        Assert.Equal(220, h.Scroll.Offset.Y);
        h.Render(project with { ChangeSequence = 2, Slips = project.Slips.Select(slip => slip.Id == "slip-0" ? slip with { Revision = 2, Text = "Changed" } : slip).ToArray() });
        Assert.Equal(220, h.Scroll.Offset.Y);
        h.Render(project with { ChangeSequence = 3 }, visible: project.Slips.Take(2).ToArray());
        Assert.Equal(0, h.Scroll.Offset.Y);
        h.Render(project with { ChangeSequence = 4 });
        h.Scroll.Offset = new Vector(0, 220);
        h.Render(project with { Id = "other" });
        Assert.Equal(0, h.Scroll.Offset.Y);
    }

    [AvaloniaFact]
    public void ReaderPresenterRetiresHiddenBlocksAndDistinguishesEmptyMessages()
    {
        using var h = new ReaderHarness();
        var project = RenderProject("reader", "one");
        h.Render(project);
        var first = h.Reader.Blocks["render-one"];
        h.Render(project, visible: []);
        Assert.Empty(h.Reader.Blocks);
        Assert.Equal("No slips match the current filters.", Assert.IsType<TextBlock>(Assert.Single(h.Panel.Children)).Text);
        PressContent(first);
        Assert.Empty(h.Selected);
        var hidden = project with { ChangeSequence = 2, Slips = project.Slips.Select(slip => slip with { ExcludedFromViews = true }).ToArray() };
        h.Render(hidden);
        Assert.Empty(h.Reader.Blocks);
        Assert.Equal("Every slip in view is hidden from views.", Assert.IsType<TextBlock>(Assert.Single(h.Panel.Children)).Text);
        h.Render(project with { ChangeSequence = 3 });
        Assert.NotSame(first, h.Reader.Blocks["render-one"]);
    }

    [AvaloniaTheory]
    [InlineData("replace")]
    [InlineData("clear")]
    [InlineData("suspend")]
    [InlineData("dispose")]
    [InlineData("project")]
    public void ReaderPresenterRetiresBlockWikiAndCheckboxActions(string change)
    {
        using var h = new ReaderHarness();
        var project = RenderProject("reader", "[[render-two|Target]]");
        project = project with { Slips = [project.Slips[0] with { BlockKind = ZetlBlockKinds.Task }, project.Slips[1]] };
        h.Render(project);
        var block = h.Reader.Blocks["render-one"];
        var link = ContentControls(block).OfType<TextBlock>().Single(text => text.Text == "Target");
        var checkbox = ContentControls(block).OfType<TextBlock>().Single(text => text.Text == "☐");
        switch (change)
        {
            case "replace": h.Render(project with { ChangeSequence = 2, Slips = [project.Slips[0] with { Revision = 2 }, project.Slips[1]] }); break;
            case "project": h.Render(project with { Id = "replacement" }); break;
            case "clear": h.Reader.Clear(); break;
            case "dispose": h.Reader.Dispose(); break;
            default: h.Reader.Suspend(); break;
        }
        PressContent(block);
        Assert.True(PressContent(link).Handled);
        Assert.True(PressContent(checkbox).Handled);
        Assert.Empty(h.Selected);
        Assert.Empty(h.Checked);
        if (change == "dispose")
        {
            h.Render(project);
            Assert.Empty(h.Reader.Blocks);
            Assert.Empty(h.Panel.Children);
        }
        else
        {
            h.Render(project, force: true);
            PressContent(h.Reader.Blocks["render-one"]);
            Assert.Equal("render-one", Assert.Single(h.Selected));
        }
    }

    [AvaloniaFact]
    public void ReaderPresenterQueuedScrollFollowsOnlyTheLatestSelection()
    {
        using var h = new ReaderHarness();
        h.Render(RenderProject("reader", "one"));
        var first = h.Reader.Blocks["render-one"];
        var second = h.Reader.Blocks["render-two"];
        var firstRequests = 0;
        var secondRequests = 0;
        first.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => firstRequests++);
        second.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => secondRequests++);
        h.Reader.UpdateSelection("render-one");
        h.Reader.UpdateSelection("render-two");
        Assert.Equal(1, firstRequests);
        Assert.Equal(1, secondRequests);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, firstRequests);
        Assert.Equal(2, secondRequests);
        Assert.Same(Brushes.Purple, second.BorderBrush);
        Assert.Same(Brushes.Transparent, first.BorderBrush);
    }

    [AvaloniaTheory]
    [InlineData("replace")]
    [InlineData("clear")]
    [InlineData("suspend")]
    [InlineData("dispose")]
    public void ReaderPresenterCancelsQueuedScrollForRetiredControls(string change)
    {
        using var h = new ReaderHarness();
        var project = RenderProject("reader", "one");
        h.Render(project);
        var block = h.Reader.Blocks["render-one"];
        var requests = 0;
        block.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => requests++);
        h.Reader.UpdateSelection("render-one");
        Assert.Equal(1, requests);
        switch (change)
        {
            case "replace": h.Render(project with { ChangeSequence = 2, Slips = [project.Slips[0] with { Revision = 2 }, project.Slips[1]] }); break;
            case "clear": h.Reader.Clear(); break;
            case "dispose": h.Reader.Dispose(); break;
            default: h.Reader.Suspend(); break;
        }
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, requests);
    }

    [AvaloniaFact]
    public async Task ReaderPresenterPendingPictureSurvivesAnUnrelatedRebuild()
    {
        var reply = new TaskCompletionSource<ZetlPictureContent?>();
        var requests = 0;
        using var h = new ReaderHarness((project, _) => { Assert.Equal("reader", project); requests++; return reply.Task; });
        var project = ReaderPictureProject();
        h.Render(project);
        var block = h.Reader.Blocks["render-one"];
        var load = ReaderPictureLoad(h.Reader, "render-one");
        var image = Assert.Single(ContentControls(block).OfType<Image>());
        h.Render(project with { ChangeSequence = 2, Slips = [project.Slips[0], project.Slips[1] with { Revision = 2, Text = "Changed" }] });
        Assert.Same(block, h.Reader.Blocks["render-one"]);
        reply.SetResult(ReaderPictureContent());
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, requests);
        Assert.Same(h.Pictures.FindDecoded("asset", 1100), image.Source);
        Assert.NotNull(image.Source);
        Assert.False(ContentControls(block).OfType<TextBlock>().Single(text => text.Text == "Loading picture…").IsVisible);
    }

    [AvaloniaTheory]
    [InlineData("replace")]
    [InlineData("filter")]
    [InlineData("clear")]
    [InlineData("project")]
    [InlineData("dispose")]
    public async Task ReaderPresenterPendingPicturesNeverAssignToRetiredBlocks(string change)
    {
        var reply = new TaskCompletionSource<ZetlPictureContent?>();
        using var h = new ReaderHarness((_, _) => reply.Task);
        var project = ReaderPictureProject();
        h.Render(project);
        var old = h.Reader.Blocks["render-one"];
        var load = ReaderPictureLoad(h.Reader, "render-one");
        switch (change)
        {
            case "replace": h.Render(project with { ChangeSequence = 2, Slips = [project.Slips[0] with { Revision = 2, Text = "new caption" }, project.Slips[1]] }); break;
            case "filter": h.Render(project, visible: [project.Slips[1]]); break;
            case "project": h.Render(project with { Id = "other", Slips = [project.Slips[1]] }); break;
            case "dispose": h.Reader.Dispose(); break;
            default: h.Reader.Clear(); break;
        }
        var currentLoad = change == "replace" ? ReaderPictureLoad(h.Reader, "render-one") : null;
        reply.SetResult(ReaderPictureContent());
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        if (currentLoad is not null)
        {
            await currentLoad.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(Assert.Single(ContentControls(h.Reader.Blocks["render-one"]).OfType<Image>()).Source);
        }
        Assert.Null(Assert.Single(ContentControls(old).OfType<Image>()).Source);
        Assert.True(ContentControls(old).OfType<TextBlock>().Single(text => text.Text == "Loading picture…").IsVisible);
        if (change != "replace") Assert.Null(h.Pictures.FindDecoded("asset", 1100));
    }

    [AvaloniaTheory]
    [InlineData("missing")]
    [InlineData("mismatch")]
    [InlineData("invalid-picture")]
    [InlineData("offline")]
    public async Task ReaderPresenterReportsPictureFailuresAndCanRetryWithACachedBitmap(string failure)
    {
        var reply = new TaskCompletionSource<ZetlPictureContent?>();
        var requests = 0;
        using var h = new ReaderHarness((_, _) => { requests++; return reply.Task; });
        var project = ReaderPictureProject();
        h.Render(project);
        var load = ReaderPictureLoad(h.Reader, "render-one");
        if (failure == "offline") reply.SetException(new IOException("Disconnected"));
        else if (failure == "invalid-picture") reply.SetException(new ArgumentException("Invalid picture"));
        else reply.SetResult(failure switch
        {
            "missing" => null, "mismatch" => ReaderPictureContent() with { Sha256 = "wrong" },
            _ => throw new InvalidOperationException("Unknown failure")
        });
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(ContentControls(h.Reader.Blocks["render-one"]).OfType<TextBlock>(), text => text.Text == "Picture unavailable.");
        using var cached = h.Pictures.AcquireDecoded(ReaderPictureContent(), 1100);
        h.Reader.Clear(); // The presenter retires its controls without disposing shared bitmaps.
        h.Render(project);
        Assert.Same(cached.Bitmap, Assert.Single(ContentControls(h.Reader.Blocks["render-one"]).OfType<Image>()).Source);
        Assert.Equal(1, requests);
    }

    [AvaloniaFact]
    public void WindowReaderReusesBlocksWhenReturningFromBoardAndRetiresOnClose()
    {
        var (window, _, _) = WorkflowWindow();
        var reader = WindowField<KastnReaderPresenter>(window, "readerPresenter");
        var block = reader.Blocks["render-one"];
        try
        {
            window.viewModeBoardButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            window.viewModeListButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(block, reader.Blocks["render-one"]);
        }
        finally { CloseWindow(window); }
        Assert.Empty(reader.Blocks);
        Assert.Empty(window.viewerDocumentPanel.Children);
    }

    private static ZetlProjectSnapshot ReaderPictureProject()
    {
        var project = RenderProject("reader", "caption");
        return project with { Slips = [project.Slips[0] with { Type = ZetlSlipType.Picture, Picture = new() { Sha256 = "asset", Width = 1, Height = 1 } }, project.Slips[1]] };
    }
    private static ZetlPictureContent ReaderPictureContent() => new()
    {
        SlipId = "render-one", Sha256 = "asset", Width = 1, Height = 1,
        Bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")
    };
    private static Task ReaderPictureLoad(KastnReaderPresenter reader, string id)
    {
        var cache = (IDictionary)typeof(KastnReaderPresenter).GetField("slipCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        return (Task)cache[id]!.GetType().GetProperty("PictureLoad")!.GetValue(cache[id])!;
    }
    private sealed class ReaderHarness : IDisposable
    {
        public readonly StackPanel Panel = new() { Spacing = 6, Margin = new Thickness(8) };
        public readonly ScrollViewer Scroll;
        public readonly Window Window;
        public readonly KastnPictureCache Pictures;
        public readonly KastnReaderPresenter Reader;
        public readonly List<string> Selected = [];
        public readonly List<string> Checked = [];
        private readonly bool ownsPictures;
        public ReaderHarness(Func<string, ZetlSlipSnapshot, Task<ZetlPictureContent?>>? fetch = null,
            KastnPictureCache? pictures = null)
        {
            Scroll = new() { Content = Panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Window = new() { Width = 600, Height = 320, Content = Scroll };
            ownsPictures = pictures is null;
            Pictures = pictures ?? new(fetch ?? ((_, _) => Task.FromResult<ZetlPictureContent?>(null)));
            Reader = new(Panel, Scroll, Pictures);
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }
        public void Render(ZetlProjectSnapshot project, IReadOnlyList<ZetlSlipSnapshot>? visible = null,
            string? selected = null, bool force = false)
        {
            var index = new KastnProjectIndex(project);
            var slips = visible ?? project.Slips;
            var view = ZetlViewDefaults.CreateAll()[0];
            var key = KastnViewRenderKey.Create(project, slips, view, new ZetlAppSettings());
            var content = new KastnSlipContentRenderer(index, ContentResources, Selected.Add,
                id => { Checked.Add(id); return Task.CompletedTask; });
            Reader.Render(new(index, slips, view, key, false, content,
                new(Brushes.Gray, Brushes.Purple, Brushes.Beige), Selected.Add), selected, force);
            Window.UpdateLayout();
        }
        public void Dispose()
        {
            Reader.Dispose();
            if (ownsPictures) Pictures.Dispose();
            Window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
