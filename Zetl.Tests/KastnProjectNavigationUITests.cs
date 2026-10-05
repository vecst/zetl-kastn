using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaTheory]
    [InlineData("activation", "unchanged")]
    [InlineData("close", "unchanged")]
    [InlineData("activation", "latest-project")]
    [InlineData("activation", "tree")]
    [InlineData("activation", "typing")]
    [InlineData("activation", "retire")]
    [InlineData("activation", "conflict")]
    public async Task ProjectNavigationSavesBeforeLeavingAndRejectsSupersededCompletions(string route, string change)
    {
        var directory = Path.Combine(Path.GetTempPath(), "KastnProjectNavigation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        using var release = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource();
        var saves = 0;
        try
        {
            var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "kastn-ui");
            var source = store.CreateProject("Source", ["Inbox", "Next"], "Inbox");
            var edited = store.AddSlip(source.Buckets[0], "baseline", "copy");
            var target = store.AddSlip(source.Buckets[1], "tree target", "copy");
            var first = store.CreateProject("First destination", ["Inbox"], "Inbox");
            store.AddSlip(first.Buckets[0], "first", "copy");
            var latest = store.CreateProject("Latest destination", ["Inbox"], "Inbox");
            store.AddSlip(latest.Buckets[0], "latest", "copy");
            var pipeName = $"kastn-project-navigation-{Guid.NewGuid():N}";
            using var server = new ZetlIpcServer(new ZetlProjectService(store), pipeName, log: null,
                dropResponseForTesting: command =>
                {
                    if (command.Kind == ZetlCommandKind.UpdateSlip)
                    {
                        Interlocked.Increment(ref saves);
                        if (arrived.TrySetResult()) release.Wait(TimeSpan.FromSeconds(5));
                    }
                    return false;
                });
            server.Start();
            await using var controller = new KastnConnectionController(
                _ => throw new InvalidOperationException("Zetl already running."), pipeName,
                TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(25));
            controller.Start(source.Id);
            await WaitForConditionAsync(() => controller.Current.Project?.Id == source.Id, "Source should load.");
            var drafts = new KastnDraftStore(Path.Combine(directory, "draft.json"));
            window = new MainWindow(controller, drafts);
            window.Show();
            window.projectTree.SelectedItem = window.treeProjection.Find(edited.Id);
            Dispatcher.UIThread.RunJobs();
            window.slipEditor.Text = "submitted edit";
            Dispatcher.UIThread.RunJobs();
            typeof(MainWindow).GetMethod("FlushDraftJournal", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
            if (change == "conflict")
            {
                edited.Text = "remote edit";
                edited.Revision++;
            }
            var before = controller.NavigationVersion;
            var navigation = route == "close" ? InvokeWorkflow(window, "CloseProjectAsync")
                : window.ActivateRequestAsync(first.Id);
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(before, controller.NavigationVersion);
            Assert.Equal(source.Id, controller.Current.Project?.Id);
            Assert.Equal(edited.Id, window.editorState.SlipId);
            Task? newer = null;
            if (change == "latest-project") newer = window.ActivateRequestAsync(latest.Id);
            else if (change == "tree") window.projectTree.SelectedItem = window.treeProjection.Find(target.Id);
            else if (change == "typing") window.slipEditor.Text = "later writing";
            else if (change == "retire") CloseWindow(window);
            if (change != "retire")
            {
                Dispatcher.UIThread.RunJobs();
                // The saved baseline can be published before the response. Keep
                // the edited slip bound until the latest navigation settles.
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(source));
                Assert.Equal(edited.Id, window.editorState.SlipId);
            }
            release.Set();
            await navigation.WaitAsync(TimeSpan.FromSeconds(5));
            if (newer is not null) await newer.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForConditionAsync(() => WindowField<Task<bool>>(window, "inflightSave").IsCompleted,
                "Shared save should settle.");
            Dispatcher.UIThread.RunJobs();
            var shouldLeave = change is "unchanged" or "latest-project";
            var destination = route == "close" ? null : change == "latest-project" ? latest.Id : first.Id;
            Assert.Equal(before + (shouldLeave ? 1 : 0), controller.NavigationVersion);
            Assert.Equal(shouldLeave ? destination : source.Id, controller.Current.Project?.Id);
            Assert.Equal(1, saves);
            Assert.Equal(change == "conflict" ? "remote edit" : "submitted edit", edited.Text);
            if (change == "tree")
            {
                Assert.Equal(target.Id, window.editorState.SlipId);
                Assert.Equal(target.Id, Assert.IsType<KastnTreeNode>(window.projectTree.SelectedItem).Id);
            }
            else if (change is "typing" or "conflict")
            {
                Assert.Equal(edited.Id, window.editorState.SlipId);
                Assert.Equal(change == "typing" ? "later writing" : "submitted edit", window.slipEditor.Text);
                Assert.True(window.editorState.IsDirty);
                Assert.NotNull(drafts.Draft);
                if (change == "conflict") Assert.NotNull(window.editorState.ConflictCurrent);
            }
            else if (shouldLeave)
            {
                Assert.Null(drafts.Draft);
                await WaitForConditionAsync(() => WindowField<ZetlProjectSnapshot?>(window, "currentProject")?.Id == destination,
                    "Window should adopt the chosen destination.");
            }
        }
        finally
        {
            release.Set();
            if (window is not null) CloseWindow(window);
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task OfflineActivationKeepsTheEditedProjectAndDraft()
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            window.slipEditor.Text = "offline draft";
            Dispatcher.UIThread.RunJobs();
            InterruptLinkWorkflow(window, controller, project, "offline");
            var before = controller.NavigationVersion;
            await window.ActivateRequestAsync("destination");
            Assert.Equal(before, controller.NavigationVersion);
            Assert.Equal(project.Id, WindowField<ZetlProjectSnapshot>(window, "currentProject").Id);
            Assert.Equal("offline draft", window.slipEditor.Text);
            Assert.True(window.editorState.IsDirty);
            Assert.Contains("Save or resolve", window.statusText.Text);
        }
        finally { CloseWindow(window); }
    }
}
