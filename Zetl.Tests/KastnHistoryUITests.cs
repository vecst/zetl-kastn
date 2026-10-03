using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaTheory]
    [InlineData("project")]
    [InlineData("reselection")]
    [InlineData("typing")]
    [InlineData("during-save")]
    public async Task DelayedUndoPreservesNewEditorContext(string interruption)
    {
        var directory = Path.Combine(Path.GetTempPath(), "KastnHistoryUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        using var releaseResponse = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource();
        var updates = 0;
        try
        {
            var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "history-ui");
            var project = store.CreateProject("History race", ["Inbox"], "Inbox");
            var first = store.AddSlip(project.Buckets[0], "baseline", "copy");
            var second = store.AddSlip(project.Buckets[0], "second baseline", "copy");
            var replacement = store.CreateProject("Other", ["Inbox"], "Inbox");
            var replacementSlip = store.AddSlip(replacement.Buckets[0], "replacement baseline", "copy");
            replacementSlip.Id = first.Id;
            store.UpdateSlip(replacementSlip, replacementSlip.Text);
            var pipeName = $"kastn-history-{Guid.NewGuid():N}";
            using var server = new ZetlIpcServer(new ZetlProjectService(store), pipeName, log: null,
                dropResponseForTesting: command =>
                {
                    if (command.Kind == ZetlCommandKind.UpdateSlip && Interlocked.Increment(ref updates) == 2)
                    {
                        arrived.SetResult();
                        releaseResponse.Wait(TimeSpan.FromSeconds(5));
                    }
                    return false;
                });
            server.Start();
            await using var controller = new KastnConnectionController(
                _ => throw new InvalidOperationException("Zetl was already running."), pipeName,
                TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(25));
            controller.Start(project.Id);
            await WaitForConditionAsync(() => controller.Current.Project?.Id == project.Id, "Project should load.");
            window = new MainWindow(controller, new KastnDraftStore(Path.Combine(directory, "draft.json")));
            window.Show();
            window.projectTree.SelectedItem = window.treeProjection.Find(first.Id);
            Dispatcher.UIThread.RunJobs();
            await InvokeWorkflow(window, "ExecuteMutationAsync", ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"), ZetlCommandKind.UpdateSlip,
                new UpdateSlipCommand { Text = "recorded change" }, project.Id, first.Id, first.Revision));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, WindowField<KastnEditHistory>(window, "editHistory").UndoCount);
            if (interruption == "during-save")
            {
                window.slipEditor.Text = "initial draft";
                Dispatcher.UIThread.RunJobs();
            }
            var undo = InvokeWorkflow(window, "UndoLastAsync");
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task? navigation = null;
            if (interruption is "project" or "during-save")
            {
                navigation = controller.NavigateToProjectAsync(replacement.Id);
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(replacement));
                window.projectTree.SelectedItem = window.treeProjection.Find(first.Id);
                Dispatcher.UIThread.RunJobs();
                window.slipEditor.Text = "replacement draft";
            }
            else
            {
                if (interruption == "reselection")
                {
                    window.projectTree.SelectedItem = window.treeProjection.Find(second.Id);
                    Dispatcher.UIThread.RunJobs();
                }
                window.slipEditor.Text = "later writing";
            }
            Dispatcher.UIThread.RunJobs();
            releaseResponse.Set();
            await undo.WaitAsync(TimeSpan.FromSeconds(5));
            if (navigation is not null) await navigation.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(interruption is "project" or "during-save" ? "replacement draft" : "later writing", window.slipEditor.Text);
            Assert.True(window.editorState.IsDirty);
            Assert.Equal(interruption == "reselection" ? second.Id : first.Id, window.editorState.SlipId);
            Assert.Equal(interruption == "during-save" ? "initial draft" : "baseline", first.Text);
            Assert.Equal("replacement baseline", replacementSlip.Text);
            Assert.Equal(2, updates); // Navigation during autosave never issues the inverse.
            Assert.DoesNotContain("Undo:", window.statusText.Text);
            Assert.False(WindowField<KastnEditHistory>(window, "editHistory").IsStepping);
        }
        finally
        {
            releaseResponse.Set();
            if (window is not null) CloseWindow(window);
            Directory.Delete(directory, recursive: true);
        }
    }
}
