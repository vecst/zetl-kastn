using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    private static ZetlProjectSnapshot InspectorProject(int count = 1000)
    {
        var project = ViewportProject(count);
        return project with { Slips = project.Slips.Select((slip, i) => slip with
        {
            Title = $"Source {i}", Text = i == 0 ? "Target" : "[[note-0|Target]]"
        }).ToArray() };
    }

    [AvaloniaFact]
    public void InspectorViewportBoundsBacklinksScrollsToEndAndRetiresEvictedActions()
    {
        using var h = new InspectorHarness();
        h.Render(InspectorProject());
        Assert.InRange(h.Presenter.RealizedBacklinkCount, 1, 50);
        var first = h.Presenter.Backlink("note-1")!;
        Assert.NotNull(first);
        Assert.Null(h.Presenter.Backlink("note-999"));
        h.Show("note-999");
        var last = h.Presenter.Backlink("note-999")!;
        Assert.NotNull(last);
        Assert.Null(h.Presenter.Backlink("note-1"));
        Assert.InRange(h.Presenter.RealizedBacklinkCount, 1, 50);
        first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(h.Selected);
        last.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["note-999"], h.Selected);
        Assert.True(h.Scroll.Offset.Y > 320);
        h.Show("note-1");
        Assert.NotNull(h.Presenter.Backlink("note-1"));
        Assert.Null(h.Presenter.Backlink("note-999"));
        Assert.True(h.Scroll.Offset.Y < 600);
    }

    [AvaloniaFact]
    public void InspectorViewportReusesMetadataAndVisibleLinksWhileOffscreenTitlesStayFresh()
    {
        using var h = new InspectorHarness();
        var project = InspectorProject();
        h.Render(project);
        h.Show("note-500");
        var button = h.Presenter.Backlink("note-500");
        Assert.True(button!.Focus());
        h.Layout();
        var controls = h.Panel.Children.ToArray();
        var offset = h.Scroll.Offset;
        project = project with { ChangeSequence = 2, Slips = project.Slips.Select(s => s.Id == "note-999"
            ? s with { Revision = 2, Title = "Fresh offscreen source" } : s).ToArray() };
        h.Render(project);
        Assert.Equal(controls, h.Panel.Children);
        Assert.Same(button, h.Presenter.Backlink("note-500"));
        Assert.True(button.IsFocused);
        Assert.InRange(Math.Abs(h.Scroll.Offset.Y - offset.Y), 0, 1);
        project = project with { ChangeSequence = 3, Slips = project.Slips.Select(s => s.Id == "note-500"
            ? s with { Revision = 2, Title = "Fresh visible source" } : s).ToArray() };
        h.Render(project);
        Assert.Same(button, h.Presenter.Backlink("note-500"));
        Assert.Equal("Fresh visible source", button!.Content);
        Assert.True(button.IsFocused);
        project = project with { ChangeSequence = 4, Slips = project.Slips.Select(s => s.Id == "note-0"
            ? s with { Revision = 2, Title = "Fresh target", Text = "Changed target" } : s).ToArray() };
        h.Render(project);
        Assert.Equal(controls, h.Panel.Children);
        Assert.Contains(h.Panel.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Fresh target");
        Assert.Contains(h.Panel.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "2");
        h.Show("note-999");
        Assert.Equal("Fresh offscreen source", h.Presenter.Backlink("note-999")!.Content);
    }

    [AvaloniaFact]
    public void InspectorViewportPreservesLogicalAnchorAcrossReordersAndClampsAfterLinksShrink()
    {
        using var h = new InspectorHarness();
        var project = InspectorProject();
        h.Render(project);
        h.Show("note-500");
        var anchor = h.Links.Where(button => button.TranslatePoint(default, h.Scroll) is { } p
                && p.Y + button.Bounds.Height > 0 && p.Y < h.Scroll.Viewport.Height)
            .OrderBy(button => button.TranslatePoint(default, h.Scroll)!.Value.Y).First();
        var id = (string)anchor.Tag!;
        var y = anchor.TranslatePoint(default, h.Scroll)!.Value.Y;
        project = project with { ChangeSequence = 2, Slips = project.Slips.Reverse().ToArray() };
        h.Render(project);
        Assert.NotNull(h.Presenter.Backlink(id));
        Assert.InRange(Math.Abs(h.Presenter.Backlink(id)!.TranslatePoint(default, h.Scroll)!.Value.Y - y), 0, 1);
        project = project with { ChangeSequence = 3, Slips = project.Slips.Where(s => s.Id is "note-0" or "note-1" or "note-2").ToArray() };
        var old = h.Presenter.Backlink(id)!;
        h.Render(project);
        Assert.Equal(2, h.Presenter.RealizedBacklinkCount);
        Assert.Empty(h.Panel.Children.OfType<KastnViewportItems>());
        Assert.True(h.Scroll.Offset.Y < 320);
        old.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(h.Selected);
        h.Render(InspectorProject() with { ChangeSequence = 4 });
        Assert.Single(h.Panel.Children.OfType<KastnViewportItems>());
        Assert.InRange(h.Presenter.RealizedBacklinkCount, 1, 50);
        h.Window.Width = 280;
        h.Layout();
        Assert.InRange(h.Presenter.RealizedBacklinkCount, 1, 50);
    }

    [AvaloniaFact]
    public void InspectorViewportKeyboardTabsThroughUnrealizedBacklinks()
    {
        using var h = new InspectorHarness();
        h.Render(InspectorProject());
        h.Show("note-1");
        Assert.True(h.Presenter.Backlink("note-1")!.Focus());
        for (var i = 2; i <= 45; i++)
        {
            h.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            h.Window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            h.Layout();
            Assert.True(h.Presenter.Backlink($"note-{i}")?.IsFocused, $"Tab should reach source {i}.");
            Assert.InRange(h.Presenter.RealizedBacklinkCount, 1, 50);
        }
        h.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
        h.Window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
        h.Layout();
        Assert.True(h.Presenter.Backlink("note-44")!.IsFocused);
    }

    [AvaloniaFact]
    public void InspectorViewportUsesCurrentUrlAndRejectsHiddenRemovedAndDisposedActions()
    {
        using var h = new InspectorHarness();
        var project = InspectorProject(3);
        project = project with { Slips = project.Slips.Select(s => s.Id == "note-0"
            ? s with { Picture = new() { Sha256 = "picture", SourceUrl = "https://example.com/first", Width = 1, Height = 1 } } : s).ToArray() };
        h.Render(project);
        var open = h.Panel.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Open URL"));
        var link = h.Presenter.Backlink("note-1")!;
        h.Presenter.Suspend(project.Id, true);
        link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(h.Selected);
        Assert.Empty(h.Opened);
        h.Render(project);
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("https://example.com/first", Assert.Single(h.Opened).AbsoluteUri);
        project = project with { ChangeSequence = 2, Slips = project.Slips.Select(s => s.Id == "note-0"
            ? s with { Revision = 2, Picture = s.Picture! with { SourceUrl = "https://example.com/second" } } : s).ToArray() };
        h.Render(project);
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("https://example.com/second", h.Opened[1].AbsoluteUri);
        project = project with { ChangeSequence = 3, Slips = project.Slips.Select(s => s.Id == "note-0"
            ? s with { Revision = 3, Picture = s.Picture! with { SourceUrl = "file:///private" } } : s).ToArray() };
        h.Render(project);
        Assert.DoesNotContain(h.Panel.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Open URL"));
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, h.Opened.Count);
        h.Render(project with { Id = "replacement" });
        link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(h.Selected);
        var current = h.Presenter.Backlink("note-1")!;
        h.Presenter.Dispose();
        current.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(h.Selected);
        Assert.Empty(h.Panel.Children);
        Assert.Equal(0, h.Presenter.RealizedBacklinkCount);
    }

    [AvaloniaFact]
    public void WindowInspectorViewportDefersHiddenRenderingAndNavigatesDistantLinks()
    {
        using var h = new ViewportWindowHarness();
        var window = h.Window;
        try
        {
            var project = InspectorProject();
            PublishRenderSnapshot(h.Controller, project);
            Assert.Empty(window.slipInspectorFieldsPanel.Children);
            window.projectTree.SelectedItem = window.treeProjection.Find("note-0");
            Dispatcher.UIThread.RunJobs();
            ClickDetails(window);
            var presenter = WindowField<KastnInspectorPresenter>(window, "inspectorPresenter");
            Assert.InRange(presenter.RealizedBacklinkCount, 1, 50);
            var old = presenter.Backlink("note-1")!;
            window.detailEditorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            old.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("note-0", ((KastnTreeNode)window.projectTree.SelectedItem!).Id);
            PublishRenderSnapshot(h.Controller, project with { ChangeSequence = 2, Slips = project.Slips.Select(s => s.Id == "note-999"
                ? s with { Title = "Newest source", Revision = 2 } : s).ToArray() });
            ClickDetails(window);
            var viewport = Assert.Single(window.slipInspectorFieldsPanel.Children.OfType<KastnViewportItems>());
            viewport.ShowSlip("note-999");
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Newest source", presenter.Backlink("note-999")!.Content);
            presenter.Backlink("note-999")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("note-999", ((KastnTreeNode)window.projectTree.SelectedItem!).Id);
            Assert.True(window.editorPanel.IsVisible);
            Assert.False(window.inspectorPanel.IsVisible);
            Assert.Equal("[[note-0|Target]]", window.slipEditor.Text);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void WindowInspectorViewportDefersBoardHiddenBacklinksAndReopensWithCurrentTitles()
    {
        using var h = new ViewportWindowHarness();
        var window = h.Window;
        try
        {
            var project = InspectorProject();
            PublishRenderSnapshot(h.Controller, project);
            window.projectTree.SelectedItem = window.treeProjection.Find("note-0");
            Dispatcher.UIThread.RunJobs();
            ClickDetails(window);
            var presenter = WindowField<KastnInspectorPresenter>(window, "inspectorPresenter");
            var old = presenter.Backlink("note-1")!;
            window.viewModeBoardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            old.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("note-0", ((KastnTreeNode)window.projectTree.SelectedItem!).Id);
            PublishRenderSnapshot(h.Controller, project with { ChangeSequence = 2, Slips = project.Slips.Select(s => s.Id == "note-1"
                ? s with { Title = "Changed in board mode", Revision = 2 } : s).ToArray() });
            var index = WindowField<KastnProjectIndex>(window, "projectIndex");
            Assert.Null(typeof(KastnProjectIndex).GetField("backlinks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(index));
            window.viewModeListButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.inspectorPanel.IsEffectivelyVisible);
            Assert.Equal("Changed in board mode", presenter.Backlink("note-1")!.Content);
            Assert.InRange(presenter.RealizedBacklinkCount, 1, 50);
        }
        finally { CloseWindow(window); }
    }

    private sealed class InspectorHarness : IDisposable
    {
        public StackPanel Panel { get; } = new() { Spacing = 8 };
        public ScrollViewer Scroll { get; }
        public Window Window { get; }
        public KastnInspectorPresenter Presenter { get; }
        public List<string> Selected { get; } = [];
        public List<Uri> Opened { get; } = [];
        public IEnumerable<Button> Links => Panel.GetVisualDescendants().OfType<Button>().Where(b => b.Tag is string);
        public InspectorHarness()
        {
            Scroll = new() { Content = Panel };
            Window = new() { Width = 520, Height = 320, Content = Scroll };
            Presenter = new(Panel, Scroll);
            Window.Show();
            Layout();
        }
        public void Render(ZetlProjectSnapshot project)
        {
            Presenter.Render(new(project), project.Slips.Single(s => s.Id == "note-0"), Selected.Add, Opened.Add);
            Layout();
        }
        public void Show(string id)
        {
            Assert.Single(Panel.Children.OfType<KastnViewportItems>()).ShowSlip(id);
            Layout();
        }
        public void Layout() { Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); Window.UpdateLayout(); }
        public void Dispose() { Presenter.Dispose(); Window.Close(); Dispatcher.UIThread.RunJobs(); }
    }
}
