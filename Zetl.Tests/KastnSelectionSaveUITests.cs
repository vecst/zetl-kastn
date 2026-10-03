using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaTheory]
    [InlineData(false, false, "unchanged")]
    [InlineData(true, false, "unchanged")]
    [InlineData(true, true, "unchanged")]
    [InlineData(true, false, "latest-selection")]
    [InlineData(true, false, "typing")]
    [InlineData(true, false, "project")]
    public async Task AutosaveSelectionKeepsClickedTargetAndProtectsLaterDrafts(bool differentBucket, bool focusSave, string change)
    {
        var directory = Path.Combine(Path.GetTempPath(), "KastnSelectionSave", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        using var release = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource<ZetlCommandEnvelope>();
        try
        {
            var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "kastn-ui");
            var project = store.CreateProject("Selection save", ["Inbox", "Next"], "Inbox");
            var inbox = project.Buckets.First(bucket => bucket.Name == "Inbox");
            var next = project.Buckets.First(bucket => bucket.Name == "Next");
            var edited = store.AddSlip(inbox, "baseline", "copy");
            // The clicked target is deliberately not the first slip in its bucket.
            var other = store.AddSlip(differentBucket ? next : inbox, "other card", "copy");
            var target = store.AddSlip(differentBucket ? next : inbox, "target card", "copy");
            var replacement = store.CreateProject("Replacement", ["Inbox"], "Inbox");
            var replacementOld = store.AddSlip(replacement.Buckets[0], "replacement old", "copy");
            replacementOld.Id = edited.Id;
            var replacementTarget = store.AddSlip(replacement.Buckets[0], "replacement target", "copy");
            replacementTarget.Id = target.Id;
            var expectedId = target.Id;
            var expectedText = target.Text;
            var pipeName = $"kastn-selection-save-{Guid.NewGuid():N}";
            using var server = new ZetlIpcServer(new ZetlProjectService(store), pipeName, log: null,
                dropResponseForTesting: command =>
                {
                    if (command.Kind == ZetlCommandKind.UpdateSlip && arrived.TrySetResult(command))
                        release.Wait(TimeSpan.FromSeconds(5));
                    return false;
                });
            server.Start();
            await using var controller = new KastnConnectionController(
                _ => throw new InvalidOperationException("Zetl already running."), pipeName,
                TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(25));
            controller.Start(project.Id);
            await WaitForConditionAsync(() => controller.Current.Project?.Id == project.Id, "Project should load.");
            var drafts = new KastnDraftStore(Path.Combine(directory, "draft.json"));
            window = new MainWindow(controller, drafts);
            window.Show();
            window.projectTree.SelectedItem = window.treeProjection.Find(edited.Id);
            Dispatcher.UIThread.RunJobs();
            window.slipEditor.Text = "saved edit";
            Dispatcher.UIThread.RunJobs();
            Task? saving = focusSave ? InvokeWorkflow(window, "SaveEditorAsync") : null;
            window.projectTree.SelectedItem = window.treeProjection.Find(target.Id);
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (change == "latest-selection")
            {
                window.projectTree.SelectedItem = window.treeProjection.Find(other.Id);
                expectedId = other.Id;
                expectedText = other.Text;
            }
            else if (change == "typing")
            {
                window.slipEditor.Text = "later writing";
                Dispatcher.UIThread.RunJobs();
                expectedId = edited.Id;
                expectedText = "later writing";
            }
            // Own-change events can arrive before the command response.
            PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(project));
            Assert.Equal(edited.Id, window.editorState.SlipId);
            Assert.Equal(change == "latest-selection" ? other.Id : target.Id, Assert.IsType<KastnTreeNode>(window.projectTree.SelectedItem).Id);
            Task? navigation = null;
            if (change == "project")
            {
                navigation = controller.NavigateToProjectAsync(replacement.Id);
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(replacement));
                window.projectTree.SelectedItem = null;
                window.projectTree.SelectedItem = window.treeProjection.Find(replacementTarget.Id);
                Dispatcher.UIThread.RunJobs();
                expectedText = replacementTarget.Text;
                Assert.Equal(replacementTarget.Id, window.editorState.SlipId);
            }
            release.Set();
            if (saving is not null) await saving.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForConditionAsync(() => WindowField<Task<bool>>(window, "inflightSave").IsCompleted,
                "Save should complete.");
            if (navigation is not null) await navigation.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("saved edit", edited.Text);
            Assert.Equal(expectedId, Assert.IsType<KastnTreeNode>(window.projectTree.SelectedItem).Id);
            Assert.Equal(expectedId, window.editorState.SlipId);
            Assert.Equal(expectedText, window.slipEditor.Text);
            Assert.Equal(change == "typing", window.editorState.IsDirty);
            if (change == "typing")
            {
                Assert.Equal("saved edit", drafts.Draft?.BaselineText);
                Assert.Equal("later writing", drafts.Draft?.DraftText);
            }
            else Assert.Null(drafts.Draft);
            Assert.Equal("replacement old", replacementOld.Text);
            Assert.Equal("replacement target", replacementTarget.Text);
            // A subsequent ordinary refresh must keep the newly accepted selection.
            await controller.SynchronizeAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(expectedId, window.editorState.SlipId);
        }
        finally
        {
            release.Set();
            if (window is not null) CloseWindow(window);
            Directory.Delete(directory, recursive: true);
        }
    }
}
