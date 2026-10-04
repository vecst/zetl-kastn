using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KASTN;
using ZETL;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaFact]
    public void TreeViewportCollapseKeepsSelectedHiddenChildAndUnsavedEditor()
    {
        using var h = new ViewportWindowHarness();
        var (window, controller) = (h.Window, h.Controller);
        try
        {
            PublishRenderSnapshot(controller, ViewportProject());
            var node = window.treeProjection.Find("note-0")!;
            window.projectTree.SelectedItem = node;
            Dispatcher.UIThread.RunJobs();
            window.slipEditor.Text = "Unsaved viewport draft";
            Dispatcher.UIThread.RunJobs();
            var version = window.editorState.SelectionVersion;
            var toggle = TreeRow(window, "render-bucket").GetVisualDescendants().OfType<ToggleButton>().Single();
            toggle.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(window.treeProjection.Find("render-bucket")!.IsExpanded);
            Assert.Same(node, window.projectTree.SelectedItem);
            Assert.Equal("Unsaved viewport draft", window.slipEditor.Text);
            Assert.True(window.editorState.IsDirty);
            Assert.Equal(version, window.editorState.SelectionVersion);
            Assert.Single(window.projectTree.GetVisualDescendants().OfType<TreeViewItem>());
            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Same(node, window.projectTree.SelectedItem);
            Assert.Equal("Unsaved viewport draft", window.slipEditor.Text);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public async Task TreeViewportNativeDragKeepsCanonicalGapAfterDistantNavigation()
    {
        using var h = new ViewportWindowHarness();
        var (window, controller) = (h.Window, h.Controller);
        try
        {
            PublishRenderSnapshot(controller, ViewportProject());
            window.projectTree.SelectedItem = window.treeProjection.Find("note-500");
            Dispatcher.UIThread.RunJobs();
            window.projectTree.ShowNode(window.treeProjection.Find("note-500")!);
            var scroll = window.projectTree.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y + 160);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            SetTreeDrag(window, "note-999");
            var row = TreeRow(window, "note-500");
            var device = (IInputDevice)Activator.CreateInstance(typeof(DragDrop).Assembly
                .GetType("Avalonia.Input.DragDropDevice")!, nonPublic: true)!;
            var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height * .8), window)!.Value;
            string? HitNodeId() => (window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors()
                .OfType<TreeViewItem>().Select(item => (item.DataContext as KastnTreeNode)?.Id).FirstOrDefault();
            // Native hit testing uses the committed composition frame, which can lag layout.
            var frames = System.Diagnostics.Stopwatch.StartNew();
            while (HitNodeId() != "note-500" && frames.Elapsed < TimeSpan.FromSeconds(2))
            {
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                await Task.Delay(10);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Equal("note-500", HitNodeId());
            NativeTreeDrag(device, window, point, TreeDragData(window), RawDragEventType.DragEnter);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(KastnDropEdge.Before, window.treeProjection.Find("note-501")!.DropEdge);
            var plan = WindowField<KastnDropPlan>(window, "stickyDropPlan");
            Assert.Equal("note-501", plan.BeforeSlipId);
            Assert.InRange(window.projectTree.GetVisualDescendants().OfType<TreeViewItem>().Count(), 1, 100);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void BoardViewportTrailingGapInMiddleOfColumnTargetsUnrealizedSuccessor()
    {
        using var h = new ViewportWindowHarness();
        var (window, controller) = (h.Window, h.Controller);
        try
        {
            var project = ViewportProject();
            PublishRenderSnapshot(controller, project);
            window.viewModeBoardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var board = WindowField<KastnBoardPresenter>(window, "boardPresenter");
            board.UpdateSelection("note-500");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var cards = board.ColumnCards("render-bucket")!;
            var last = board.RealizedColumnCards("render-bucket")
                .OrderBy(c => c.TranslatePoint(default, cards)!.Value.Y).Last();
            var id = ((KastnTreeNode)last.DataContext!).Id;
            var next = project.Slips.SkipWhile(s => s.Id != id).Skip(1).First().Id;
            Assert.Null(board.Card(next));
            SetTreeDrag(window, "note-999");
            var args = new DragEventArgs(DragDrop.DragOverEvent, TreeDragData(window), cards,
                new Point(10, last.TranslatePoint(default, cards)!.Value.Y + last.Bounds.Height + 2), KeyModifiers.None);
            var position = (KastnDropPosition)typeof(MainWindow).GetMethod("BoardColumnSlipPosition", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [args, "render-bucket", new HashSet<string>(StringComparer.Ordinal) { "note-999" }])!;
            Assert.Equal(next, position.BeforeSlipId);
            Assert.Equal(id, position.MarkerId);
        }
        finally { CloseWindow(window); }
    }
    private sealed class ViewportWindowHarness : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "KastnViewportUI", Guid.NewGuid().ToString("N"));
        public KastnConnectionController Controller { get; } = new(_ => Task.CompletedTask);
        public MainWindow Window { get; }
        public ViewportWindowHarness()
        {
            Directory.CreateDirectory(directory);
            var project = RenderProject("viewport-window", "baseline");
            typeof(KastnConnectionController).GetProperty(nameof(KastnConnectionController.Current))!
                .SetValue(Controller, new KastnSessionSnapshot(KastnConnectionState.Online, "Connected", [], project));
            Window = new(Controller, new KastnDraftStore(Path.Combine(directory, "draft.json")),
                new ZetlViewStore(Path.Combine(directory, "views")), new KastnSettings(Path.Combine(directory, "settings.json")),
                new ZetlTemplateStore(Path.Combine(directory, "templates")), new ZetlCreationTypeStore(Path.Combine(directory, "creations")),
                new KastnStateStore(Path.Combine(directory, "state.json")));
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }
        public void Dispose()
        {
            Controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Directory.Delete(directory, recursive: true);
        }
    }
}
