using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaFact]
    public void IncrementalWireEditPreservesTreeViewportSelectionFocusAndUnsavedDraft()
    {
        using var h = new ViewportWindowHarness();
        var window = h.Window;
        try
        {
            var project = InspectorProject();
            PublishRenderSnapshot(h.Controller, project);
            window.projectTree.SelectedItem = window.treeProjection.Find("note-500");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            window.slipEditor.Text = "Unsaved local writing";
            Assert.True(window.slipEditor.Focus());
            Dispatcher.UIThread.RunJobs();
            var row = TreeRow(window, "note-500");
            var items = window.projectTree.ItemsSource;
            var selection = window.projectTree.SelectedItem;
            var version = window.editorState.SelectionVersion;
            project = project with { ChangeSequence = 2, Slips = project.Slips.Select(s => s.Id == "note-999"
                ? s with { Revision = 2, Title = "Remote source", ExcludedFromViews = true } : s).ToArray() };
            project = JsonSerializer.Deserialize<ZetlProjectSnapshot>(JsonSerializer.Serialize(project))!;
            PublishRenderSnapshot(h.Controller, project);
            window.UpdateLayout();
            Assert.Same(items, window.projectTree.ItemsSource);
            Assert.Same(row, TreeRow(window, "note-500"));
            Assert.Same(selection, window.projectTree.SelectedItem);
            Assert.True(window.slipEditor.IsFocused);
            Assert.Equal("Unsaved local writing", window.slipEditor.Text);
            Assert.True(window.editorState.IsDirty);
            Assert.Equal(version, window.editorState.SelectionVersion);
            Assert.Equal("Remote source", window.treeProjection.Find("note-999")!.Label);
            Assert.Equal(1, window.treeProjection.Find("render-bucket")!.HiddenCount);
            Assert.Same(project.Slips[500], ((KastnTreeNode)selection!).Slip);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void WindowReusesBacklinksAcrossHiddenUpdatesButResetsOnServerRestart()
    {
        using var h = new ViewportWindowHarness();
        var window = h.Window;
        try
        {
            var project = InspectorProject(200);
            void Publish(string server)
            {
                var snapshot = new KastnSessionSnapshot(KastnConnectionState.Online, "Connected", [], project) { ServerInstanceId = server };
                typeof(MainWindow).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [snapshot]);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
            Publish("server-a");
            window.projectTree.SelectedItem = window.treeProjection.Find("note-0");
            Dispatcher.UIThread.RunJobs();
            ClickDetails(window);
            var first = WindowField<KastnProjectIndex>(window, "projectIndex");
            var original = first.Backlinks;
            var presenter = WindowField<KastnInspectorPresenter>(window, "inspectorPresenter");
            var originalButton = presenter.Backlink("note-1")!;
            Assert.Equal(200, first.ParsedBacklinkSourceCount);
            window.detailEditorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            project = project with { ChangeSequence = 2, Slips = project.Slips.Select(s => s.Id == "note-199"
                ? s with { Revision = 2, Text = "Removed link" } : s).ToArray() };
            Publish("server-a");
            var next = WindowField<KastnProjectIndex>(window, "projectIndex");
            Assert.Equal(0, next.ParsedBacklinkSourceCount);
            ClickDetails(window);
            Assert.Equal(198, next.Backlinks["note-0"].Count);
            Assert.Equal(1, next.ParsedBacklinkSourceCount);
            Publish("server-b");
            var restarted = WindowField<KastnProjectIndex>(window, "projectIndex");
            Assert.NotSame(next, restarted);
            Assert.Equal(200, restarted.ParsedBacklinkSourceCount);
            originalButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("note-0", ((KastnTreeNode)window.projectTree.SelectedItem!).Id);
            Assert.Equal(199, original["note-0"].Count);
        }
        finally { CloseWindow(window); }
    }
}
