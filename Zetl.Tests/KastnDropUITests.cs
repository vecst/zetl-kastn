using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaTheory]
    [InlineData("unchanged")]
    [InlineData("typing-move")]
    [InlineData("typing-reorder")]
    [InlineData("project-move")]
    [InlineData("project-save")]
    [InlineData("draft-save")]
    [InlineData("capture-save")]
    [InlineData("anchor-removed")]
    [InlineData("conflict")]
    [InlineData("reselection")]
    [InlineData("bucket")]
    public async Task DropsCaptureIntentPreserveDraftsAndStopUnsentCommands(string scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), "KastnDropUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        using var releaseResponse = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource<ZetlCommandEnvelope>();
        var commands = new ConcurrentQueue<ZetlCommandEnvelope>();
        try
        {
            var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "kastn-ui");
            var project = store.CreateProject("Drop race", ["Inbox", "Next"], "Inbox");
            var inbox = project.Buckets.First(b => b.Name == "Inbox");
            var next = project.Buckets.First(b => b.Name == "Next");
            var first = store.AddSlip(inbox, "baseline", "copy");
            var second = store.AddSlip(inbox, "other baseline", "copy");
            var anchor = store.AddSlip(next, "anchor", "copy");
            var replacement = store.CreateProject("Other", ["Inbox"], "Inbox");
            var replacementSlip = store.AddSlip(replacement.Buckets[0], "replacement baseline", "copy");
            replacementSlip.Id = first.Id;
            store.UpdateSlip(replacementSlip, replacementSlip.Text);
            var savingFirst = scenario.EndsWith("-save", StringComparison.Ordinal);
            var bucket = scenario == "bucket";
            var delayedKind = savingFirst ? ZetlCommandKind.UpdateSlip
                : bucket ? ZetlCommandKind.UpdateBucket
                : scenario == "typing-reorder" ? ZetlCommandKind.ReorderSlip : ZetlCommandKind.MoveSlip;
            var pipeName = $"kastn-drop-{Guid.NewGuid():N}";
            using var server = new ZetlIpcServer(new ZetlProjectService(store), pipeName, log: null,
                dropResponseForTesting: command =>
                {
                    if (command.Kind is ZetlCommandKind.UpdateSlip or ZetlCommandKind.MoveSlip
                        or ZetlCommandKind.ReorderSlip or ZetlCommandKind.UpdateBucket or ZetlCommandKind.ReorderBucket)
                    {
                        commands.Enqueue(command);
                        if (command.Kind == delayedKind && arrived.TrySetResult(command))
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
            var drafts = new KastnDraftStore(Path.Combine(directory, "draft.json"));
            window = new MainWindow(controller, drafts);
            window.Show();
            window.projectTree.SelectedItem = window.treeProjection.Find(first.Id);
            Dispatcher.UIThread.RunJobs();
            var index = new KastnProjectIndex(ZetlProjectSnapshotMapper.ToSnapshot(project));
            var plan = bucket
                ? KastnDropPlanner.Plan(index, inbox.Id, false, [], new(KastnDropTargetKind.Bucket, next.Id))!
                : KastnDropPlanner.Plan(index, first.Id, true, [second.Id, first.Id],
                    new(KastnDropTargetKind.Slip, anchor.Id, KastnDropEdge.Before))!;
            var capturedIds = plan.SlipIds.ToList();
            plan = plan with { SlipIds = capturedIds };
            if (savingFirst)
            {
                window.slipEditor.Text = "initial draft";
                Dispatcher.UIThread.RunJobs();
            }
            var dropping = InvokeWorkflow(window, "ApplyDropAsync", plan);
            await Task.WhenAny(dropping, arrived.Task).WaitAsync(TimeSpan.FromSeconds(5));
            if (dropping.IsCompleted) await dropping;
            Assert.True(arrived.Task.IsCompleted, $"Drop stopped before submission: {window.statusText.Text}");
            Task? navigation = null;
            if (scenario.StartsWith("project-", StringComparison.Ordinal))
            {
                navigation = controller.NavigateToProjectAsync(replacement.Id);
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(replacement));
                window.projectTree.SelectedItem = window.treeProjection.Find(first.Id);
                Dispatcher.UIThread.RunJobs();
                window.slipEditor.Text = "replacement draft";
            }
            else if (scenario == "reselection")
            {
                window.projectTree.SelectedItem = window.treeProjection.Find(anchor.Id);
                Dispatcher.UIThread.RunJobs();
                window.slipEditor.Text = "other draft";
            }
            else if (scenario is "typing-move" or "typing-reorder" or "draft-save" or "conflict")
            {
                window.slipEditor.Text = "later writing";
                Dispatcher.UIThread.RunJobs();
                window.editorState.SetInlineStyles([new() { Start = 0, Length = 5, Kind = ZetlInlineStyleKinds.Italic }]);
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(project));
                Assert.Null(window.editorState.ConflictCurrent);
                if (scenario == "conflict")
                {
                    store.UpdateSlip(first, "remote writing");
                    PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(project));
                    Assert.NotNull(window.editorState.ConflictCurrent);
                }
            }
            else if (scenario == "capture-save")
            {
                capturedIds.Clear();
                capturedIds.Add(anchor.Id); // The already submitted drop must retain its own IDs.
            }
            else if (scenario == "anchor-removed")
            {
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(project) with
                    { Slips = ZetlProjectSnapshotMapper.ToSnapshot(project).Slips.Where(s => s.Id != anchor.Id).ToArray() });
            }
            Dispatcher.UIThread.RunJobs();
            releaseResponse.Set();
            await dropping.WaitAsync(TimeSpan.FromSeconds(5));
            if (navigation is not null) await navigation.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();

            var completed = scenario is "unchanged" or "typing-move" or "typing-reorder" or "capture-save";
            Assert.All(commands, command => Assert.Equal(project.Id, command.ProjectId));
            Assert.Equal(completed ? savingFirst ? 5 : 4 : 1, commands.Count);
            Assert.False(WindowField<bool>(window, "savingCore"));
            Assert.False(WindowField<bool>(window, "applyingDrop"));
            Assert.Equal(scenario is "project-save" or "draft-save" or "bucket", inbox.Slips.Contains(first));
            Assert.Equal(!completed, inbox.Slips.Contains(second));
            Assert.Equal("replacement baseline", replacementSlip.Text);
            var history = WindowField<KastnEditHistory>(window, "editHistory");
            Assert.Equal(scenario.StartsWith("project-", StringComparison.Ordinal) ? 0 : scenario == "capture-save" ? 2 : 1, history.UndoCount);
            if (completed)
            {
                Assert.Equal(new[] { first.Id, second.Id, anchor.Id }, next.Slips.Select(s => s.Id));
                Assert.Equal(2, history.Peek()!.Operations.Count);
                Assert.Equal(second.Id, history.Peek()!.Operations.Single(op => op.SlipId == first.Id).FromFollowing);
                Assert.Equal(anchor.Id, history.Peek()!.Operations.Single(op => op.SlipId == second.Id).FromFollowing);
            }
            if (scenario is "typing-move" or "typing-reorder" or "draft-save" or "conflict")
            {
                Assert.Equal("later writing", window.slipEditor.Text);
                Assert.True(window.editorState.IsDirty);
                Assert.Equal(scenario == "conflict", window.editorState.ConflictCurrent is not null);
                Assert.Equal(completed ? 3 : 2, drafts.Draft?.BaselineRevision);
                Assert.Equal("later writing", drafts.Draft?.DraftText);
                Assert.Equal(ZetlInlineStyleKinds.Italic, Assert.Single(drafts.Draft!.DraftInlineStyles).Kind);
            }
            else if (scenario is "project-move" or "project-save" or "reselection")
            {
                Assert.Equal(scenario == "reselection" ? "other draft" : "replacement draft", window.slipEditor.Text);
                Assert.True(window.editorState.IsDirty);
                Assert.Null(window.editorState.ConflictCurrent);
            }
            else if (scenario == "bucket")
            {
                Assert.Equal(next.Id, inbox.ParentBucketId);
                Assert.Same(window.treeProjection.Find(inbox.Id), window.projectTree.SelectedItem);
                await InvokeWorkflow(window, "UndoLastAsync");
                Assert.Null(inbox.ParentBucketId);
                await InvokeWorkflow(window, "RedoLastAsync");
                Assert.Equal(next.Id, inbox.ParentBucketId);
            }
            else if (scenario is "unchanged" or "capture-save")
            {
                Assert.False(window.editorState.IsDirty);
                Assert.Null(drafts.Draft);
                await InvokeWorkflow(window, "UndoLastAsync");
                Assert.Equal(new[] { first.Id, second.Id }, inbox.Slips.Select(s => s.Id));
                await InvokeWorkflow(window, "RedoLastAsync");
                Assert.Equal(new[] { first.Id, second.Id, anchor.Id }, next.Slips.Select(s => s.Id));
            }
        }
        finally
        {
            releaseResponse.Set();
            if (window is not null) CloseWindow(window);
            Directory.Delete(directory, recursive: true);
        }
    }
}
