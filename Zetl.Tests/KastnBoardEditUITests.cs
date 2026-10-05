using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    public static IEnumerable<object[]> BoardDialogInterruptions =>
        from delete in new[] { false, true }
        from interruption in new[] { "typing", "styles", "pending-style", "reselection", "project", "remote", "offline", "other-target" }
        select new object[] { delete, interruption };

    [AvaloniaTheory]
    [MemberData(nameof(BoardDialogInterruptions))]
    public async Task BoardDialogRejectsChangedDraftSessionOrTarget(bool delete, string interruption)
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            var target = project.Slips[interruption == "other-target" ? 1 : 0];
            var reply = new TaskCompletionSource<EditSlipResult?>();
            var editing = InvokeWorkflow(window, "EditBoardSlipAsync", target,
                (Func<ZetlSlipSnapshot, IReadOnlyList<ZetlBucketSnapshot>, Task<EditSlipResult?>>)((_, _) => reply.Task));
            if (interruption == "other-target")
                PublishRenderSnapshot(controller, project with
                    { Slips = [project.Slips[0], target with { Revision = 2, Text = "remote target" }] });
            else
                InterruptLinkWorkflow(window, controller, project, interruption);
            var text = window.slipEditor.Text;
            var styles = window.editorState.DraftInlineStyles.ToArray();
            var version = window.editorState.SelectionVersion;
            var status = window.statusText.Text;
            reply.SetResult(new() { Save = !delete, Delete = delete, Text = "dialog text" });
            await editing;
            Assert.Equal(text, window.slipEditor.Text);
            Assert.Equal(styles, window.editorState.DraftInlineStyles);
            Assert.Equal(version, window.editorState.SelectionVersion);
            Assert.Equal(status, window.statusText.Text); // Unexpected IPC would report offline.
            Assert.False(WindowField<bool>(window, "saving"));
            Assert.False(WindowField<bool>(window, "boardEditing"));
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public async Task BoardDialogWaitsForSuccessfulEditorSave()
    {
        var (window, _, project) = WorkflowWindow();
        try
        {
            window.slipEditor.Text = "unsaved writing";
            Dispatcher.UIThread.RunJobs();
            await InvokeWorkflow(window, "EditBoardSlipAsync", project.Slips[0],
                (Func<ZetlSlipSnapshot, IReadOnlyList<ZetlBucketSnapshot>, Task<EditSlipResult?>>)((_, _) =>
                    throw new InvalidOperationException("The dialog must wait for a successful save.")));
            Assert.True(window.editorState.IsDirty);
            Assert.Equal("unsaved writing", window.slipEditor.Text);
            Assert.Equal("baseline", window.editorState.BaselineText);
            Assert.StartsWith("Slip was not saved", window.statusText.Text);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public async Task BoardDialogPreventsOverlappingEditWorkflows()
    {
        var (window, _, project) = WorkflowWindow();
        try
        {
            var reply = new TaskCompletionSource<EditSlipResult?>();
            var editing = InvokeWorkflow(window, "EditBoardSlipAsync", project.Slips[0],
                (Func<ZetlSlipSnapshot, IReadOnlyList<ZetlBucketSnapshot>, Task<EditSlipResult?>>)((_, _) => reply.Task));
            await InvokeWorkflow(window, "EditBoardSlipAsync", project.Slips[1],
                (Func<ZetlSlipSnapshot, IReadOnlyList<ZetlBucketSnapshot>, Task<EditSlipResult?>>)((_, _) =>
                    throw new InvalidOperationException("A second dialog must not open.")));
            Assert.True(WindowField<bool>(window, "boardEditing"));
            reply.SetResult(null);
            await editing;
            Assert.False(WindowField<bool>(window, "boardEditing"));
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public async Task BoardDialogResolvesStaleCardAndRejectsRemovedDestination()
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            var fresh = project.Slips[0] with { Revision = 2, Text = "fresh text" };
            PublishRenderSnapshot(controller, project with { Slips = [fresh, project.Slips[1]] });
            await InvokeWorkflow(window, "EditBoardSlipAsync", project.Slips[0],
                (Func<ZetlSlipSnapshot, IReadOnlyList<ZetlBucketSnapshot>, Task<EditSlipResult?>>)((target, _) =>
                {
                    Assert.Equal(fresh, target);
                    return Task.FromResult<EditSlipResult?>(new() { Save = true, Text = "dialog text", DestinationBucketId = "removed" });
                }));
            Assert.Equal("fresh text", window.slipEditor.Text);
            Assert.False(window.editorState.IsDirty);
            Assert.NotEqual("Zetl is offline.", window.statusText.Text);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public async Task BoardDialogSettlesSavedBaselineBeforeQueuedUiRefresh()
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            var saved = project.Slips[0] with { Revision = 2, Text = "saved writing" };
            window.editorState.AcceptSaved(saved);
            window.slipEditor.Text = saved.Text;
            Dispatcher.UIThread.RunJobs();
            var version = window.editorState.SelectionVersion;
            typeof(KastnConnectionController).GetProperty(nameof(KastnConnectionController.Current))!
                .SetValue(controller, new KastnSessionSnapshot(KastnConnectionState.Online, "Connected", [],
                    project with { Slips = [saved, project.Slips[1]] }));
            var opened = false;
            await InvokeWorkflow(window, "EditBoardSlipAsync", project.Slips[0],
                (Func<ZetlSlipSnapshot, IReadOnlyList<ZetlBucketSnapshot>, Task<EditSlipResult?>>)((target, _) =>
                {
                    opened = true;
                    Assert.Equal(saved, target);
                    return Task.FromResult<EditSlipResult?>(null);
                }));
            Assert.True(opened);
            Assert.Equal(version, window.editorState.SelectionVersion);
            Assert.Equal(saved.Text, window.slipEditor.Text);
        }
        finally { CloseWindow(window); }
    }

    public static IEnumerable<object[]> BoardCommandInterruptions =>
        (from command in new[] { "update", "move", "delete" }
         from interruption in new[] { "unchanged", "typing", "reselection", "project" }
         select new object[] { command, interruption })
        .Concat([new object[] { "move", "focus-save" }, new object[] { "move", "during-save" }, new object[] { "update", "conflict" }]);

    [AvaloniaTheory]
    [MemberData(nameof(BoardCommandInterruptions))]
    public async Task BoardCommandsPreserveDraftsAndStopUnsentEdits(string kind, string interruption)
    {
        var directory = Path.Combine(Path.GetTempPath(), "KastnBoardUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        using var releaseResponse = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource<ZetlCommandEnvelope>();
        var commands = new ConcurrentQueue<ZetlCommandEnvelope>();
        try
        {
            var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "kastn-ui");
            var project = store.CreateProject("Board race", ["Inbox", "Next"], "Inbox");
            var inbox = project.Buckets.First(bucket => bucket.Name == "Inbox");
            var next = project.Buckets.First(bucket => bucket.Name == "Next");
            var first = store.AddSlip(inbox, "baseline", "copy");
            var second = store.AddSlip(inbox, "other baseline", "copy");
            var replacement = store.CreateProject("Other", ["Inbox"], "Inbox");
            var replacementSlip = store.AddSlip(replacement.Buckets[0], "replacement baseline", "copy");
            replacementSlip.Id = first.Id;
            store.UpdateSlip(replacementSlip, replacementSlip.Text);
            var initial = ZetlProjectSnapshotMapper.ToSnapshot(project).Slips.First(slip => slip.Id == first.Id);
            var delayedKind = interruption == "during-save" ? ZetlCommandKind.UpdateSlip : kind switch
            {
                "move" => ZetlCommandKind.MoveSlip,
                "delete" => ZetlCommandKind.DeleteSlip,
                _ => ZetlCommandKind.UpdateSlip
            };
            var pipeName = $"kastn-board-{Guid.NewGuid():N}";
            using var server = new ZetlIpcServer(new ZetlProjectService(store), pipeName, log: null,
                dropResponseForTesting: command =>
                {
                    if (command.Kind is ZetlCommandKind.UpdateSlip or ZetlCommandKind.MoveSlip or ZetlCommandKind.DeleteSlip)
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
            if (interruption is "focus-save" or "during-save")
            {
                window.slipEditor.Text = "initial draft";
                Dispatcher.UIThread.RunJobs();
                if (interruption == "focus-save") _ = InvokeWorkflow(window, "SaveEditorAsync");
            }
            var dialogOpened = false;
            var startingVersion = window.editorState.SelectionVersion;
            var editing = InvokeWorkflow(window, "EditBoardSlipAsync", initial,
                (Func<ZetlSlipSnapshot, IReadOnlyList<ZetlBucketSnapshot>, Task<EditSlipResult?>>)((target, _) =>
                {
                    dialogOpened = true;
                    Assert.Equal(interruption == "focus-save" ? "initial draft" : "baseline", target.Text);
                    return Task.FromResult<EditSlipResult?>(new()
                    {
                        Save = kind != "delete", Delete = kind == "delete", Text = "dialog text",
                        DestinationBucketId = kind == "move" ? next.Id : inbox.Id,
                        BlockKind = ZetlBlockKinds.Quote, IgnoreBucketRenderKind = true
                    });
                }));
            await Task.WhenAny(editing, arrived.Task).WaitAsync(TimeSpan.FromSeconds(5));
            if (editing.IsCompleted) await editing;
            Assert.True(arrived.Task.IsCompleted,
                $"Board edit stopped before submission: dialog={dialogOpened}, version={startingVersion}/{window.editorState.SelectionVersion}, dirty={window.editorState.IsDirty}, status={window.statusText.Text}");
            Task? navigation = null;
            if (interruption == "project")
            {
                navigation = controller.NavigateToProjectAsync(replacement.Id);
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(replacement));
                window.projectTree.SelectedItem = window.treeProjection.Find(first.Id);
                Dispatcher.UIThread.RunJobs();
                window.slipEditor.Text = "replacement draft";
            }
            else if (interruption == "reselection")
            {
                window.projectTree.SelectedItem = window.treeProjection.Find(second.Id);
                Dispatcher.UIThread.RunJobs();
                window.slipEditor.Text = "other draft";
            }
            else if (interruption is "typing" or "conflict" or "during-save")
            {
                window.slipEditor.Text = "later writing";
                Dispatcher.UIThread.RunJobs();
                window.editorState.SetInlineStyles([new() { Start = 0, Length = 5, Kind = ZetlInlineStyleKinds.Italic }]);
                // Own change can arrive as a snapshot before its IPC response.
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(project));
                Assert.Null(window.editorState.ConflictCurrent);
                if (interruption == "conflict")
                {
                    store.UpdateSlip(first, "remote writing");
                    PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(project));
                }
            }
            Dispatcher.UIThread.RunJobs();
            releaseResponse.Set();
            await editing.WaitAsync(TimeSpan.FromSeconds(5));
            if (navigation is not null) await navigation.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();

            Assert.All(commands, command => Assert.Equal(project.Id, command.ProjectId));
            Assert.Equal(interruption != "during-save", dialogOpened);
            var completesMove = kind == "move" && interruption is "unchanged" or "focus-save";
            Assert.Equal(completesMove ? interruption == "focus-save" ? 3 : 2 : 1, commands.Count);
            Assert.Equal(kind == "update" ? interruption == "conflict" ? "remote writing" : "dialog text"
                : completesMove ? "dialog text" : interruption == "during-save" ? "initial draft" : "baseline", first.Text);
            Assert.Equal("replacement baseline", replacementSlip.Text);
            Assert.False(WindowField<bool>(window, "saving"));
            Assert.False(WindowField<bool>(window, "boardEditing"));
            var history = WindowField<KastnEditHistory>(window, "editHistory");
            Assert.Equal(interruption == "project" ? 0 : interruption == "focus-save" ? 2 : 1, history.UndoCount);
            if (completesMove)
            {
                var entry = history.Peek();
                Assert.NotNull(entry);
                var undo = Assert.Single(entry!.Operations);
                Assert.Equal(inbox.Id, undo.To.Restore!.BucketId);
                Assert.Equal(next.Id, undo.From.BucketId);
                Assert.Equal("dialog text", undo.From.Text);
            }
            if (interruption is "unchanged" or "focus-save")
            {
                Assert.Equal(kind == "delete" ? null : "dialog text", window.editorState.SlipId is null ? null : window.slipEditor.Text);
                Assert.False(window.editorState.IsDirty);
                Assert.Null(drafts.Draft);
            }
            else
            {
                Assert.Equal(interruption switch
                {
                    "project" => "replacement draft", "reselection" => "other draft", _ => "later writing"
                }, window.slipEditor.Text);
                Assert.True(window.editorState.IsDirty);
                // A queued snapshot may show the draft status after the command
                // showed its conflict status. Both must preserve the conflict.
                Assert.True(window.statusText.Text?.StartsWith("Unsaved changes", StringComparison.Ordinal) == true
                    || window.statusText.Text?.StartsWith("Resolve the slip conflict", StringComparison.Ordinal) == true);
                Assert.Equal(interruption == "conflict", window.editorState.ConflictCurrent is not null);
                if (interruption is "typing" or "conflict" or "during-save")
                {
                    Assert.Equal(initial.Revision + 1, drafts.Draft?.BaselineRevision);
                    Assert.Equal(kind == "update" ? "dialog text" : interruption == "during-save" ? "initial draft" : "baseline", drafts.Draft?.BaselineText);
                    Assert.Equal("later writing", drafts.Draft?.DraftText);
                    Assert.Equal(ZetlInlineStyleKinds.Italic, Assert.Single(drafts.Draft!.DraftInlineStyles).Kind);
                }
            }
            if (kind == "move" && interruption == "unchanged")
            {
                await InvokeWorkflow(window, "UndoLastAsync");
                Dispatcher.UIThread.RunJobs();
                Assert.Contains(first, inbox.Slips);
                Assert.Equal("baseline", first.Text);
                await InvokeWorkflow(window, "RedoLastAsync");
                Dispatcher.UIThread.RunJobs();
                Assert.Contains(first, next.Slips);
                Assert.Equal("dialog text", first.Text);
                Assert.Equal(ZetlBlockKinds.Quote, first.BlockKind);
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
