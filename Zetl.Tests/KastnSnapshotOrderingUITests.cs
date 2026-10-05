using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaFact]
    public void SnapshotOrderingKeepsLatestTreeAndPreservesEditorFocusSelectionAndWriting()
    {
        var (window, _, project) = WorkflowWindow();
        try
        {
            var owner = WindowField<KastnSnapshotCoordinator>(window, "snapshots");
            owner.ApplyNow(OrderedSnapshot(project, 1));
            var selection = window.projectTree.SelectedItem;
            window.slipEditor.Text = "local writing";
            Assert.True(window.slipEditor.Focus());
            Dispatcher.UIThread.RunJobs();
            var token = window.editorState.SelectionVersion;
            var older = project with { ChangeSequence = 2, Slips = [project.Slips[0], project.Slips[1] with { Title = "older", Revision = 2 }] };
            var latest = project with { ChangeSequence = 3, Slips = [project.Slips[0], project.Slips[1] with { Title = "latest", Revision = 3 }] };
            owner.Queue(OrderedSnapshot(older, 2));
            owner.ApplyNow(OrderedSnapshot(latest, 3));
            owner.Queue(OrderedSnapshot(older, 2));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(latest, WindowField<ZetlProjectSnapshot>(window, "currentProject"));
            Assert.Equal("latest", window.treeProjection.Find("render-two")!.Label);
            Assert.Same(selection, window.projectTree.SelectedItem);
            Assert.Equal("local writing", window.slipEditor.Text);
            Assert.True(window.slipEditor.IsFocused);
            Assert.True(window.editorState.IsDirty);
            Assert.Equal(token, window.editorState.SelectionVersion);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void CoalescedProjectRoundTripRetiresNativeActionsAndKeepsSameProjectDraft()
    {
        var (window, _, project) = WorkflowWindow();
        try
        {
            var owner = WindowField<KastnSnapshotCoordinator>(window, "snapshots");
            owner.ApplyNow(OrderedSnapshot(project, 1));
            var reader = WindowField<KastnReaderPresenter>(window, "readerPresenter");
            var old = reader.Blocks["render-two"];
            var history = WindowField<KastnEditHistory>(window, "editHistory");
            var generation = history.Generation;
            window.slipEditor.Text = "surviving draft";
            Dispatcher.UIThread.RunJobs();
            var token = window.editorState.SelectionVersion;
            owner.Queue(OrderedSnapshot(project with { Id = "other-project" }, 2));
            owner.Queue(OrderedSnapshot(project, 3));
            Dispatcher.UIThread.RunJobs();
            Assert.True(history.Generation > generation);
            Assert.True(window.editorState.SelectionVersion > token);
            Assert.Equal("surviving draft", window.slipEditor.Text);
            Assert.Equal("render-one", window.editorState.SlipId);
            Assert.Null(window.editorState.ConflictCurrent);
            Assert.NotSame(old, reader.Blocks["render-two"]);
            PressContent(old);
            Assert.Equal("render-one", window.editorState.SlipId);
            Assert.Equal("render-one", Assert.IsType<KastnTreeNode>(window.projectTree.SelectedItem).Id);
        }
        finally { CloseWindow(window); }
    }

    private static KastnSessionSnapshot OrderedSnapshot(ZetlProjectSnapshot project, long publication) =>
        new(KastnConnectionState.Online, "Connected", [], project)
        { PublicationVersion = publication, ServerInstanceId = "snapshot-server" };
}
