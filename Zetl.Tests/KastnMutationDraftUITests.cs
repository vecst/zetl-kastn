using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaTheory]
    [InlineData("move-typing")]
    [InlineData("delete-typing")]
    [InlineData("delete-confirm-navigation")]
    [InlineData("delete-confirm-typing")]
    [InlineData("add-save-navigation")]
    [InlineData("divider-save-navigation")]
    public async Task MutationPrerequisitesKeepOriginalTargetsAndSentChangesPreserveNewWriting(string scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), "KastnMutationDraft", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        using var release = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource();
        var confirming = new TaskCompletionSource();
        var answer = new TaskCompletionSource<bool>();
        var commands = new ConcurrentQueue<ZetlCommandEnvelope>();
        try
        {
            var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "kastn-ui");
            var project = store.CreateProject("Source", ["Inbox", "Next"], "Inbox");
            var first = store.AddSlip(project.Buckets[0], "baseline", "copy");
            var next = project.Buckets[1];
            var replacement = store.CreateProject("Replacement", ["Inbox"], "Inbox");
            var other = store.AddSlip(replacement.Buckets[0], "replacement", "copy");
            var pipe = $"kastn-mutation-draft-{Guid.NewGuid():N}";
            using var server = new ZetlIpcServer(new ZetlProjectService(store), pipe, log: null,
                dropResponseForTesting: command =>
                {
                    if (command.Kind is ZetlCommandKind.UpdateSlip or ZetlCommandKind.MoveSlip
                        or ZetlCommandKind.DeleteSlip or ZetlCommandKind.AddSlip or ZetlCommandKind.ReorderSlip)
                    {
                        commands.Enqueue(command);
                        if (arrived.TrySetResult()) release.Wait(TimeSpan.FromSeconds(5));
                    }
                    return false;
                });
            server.Start();
            await using var controller = new KastnConnectionController(
                _ => throw new InvalidOperationException("Already running."), pipe,
                TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(25));
            controller.Start(project.Id);
            await WaitForConditionAsync(() => controller.Current.Project?.Id == project.Id, "Source should load.");
            var drafts = new KastnDraftStore(Path.Combine(directory, "draft.json"));
            window = new MainWindow(controller, drafts);
            window.Show();
            window.projectTree.SelectedItem = window.treeProjection.Find(first.Id);
            Dispatcher.UIThread.RunJobs();
            var destination = WindowField<ComboBox>(window, "moveBucketBox");
            destination.SelectedItem = destination.ItemsSource!.Cast<KastnBucketItem>().Single(item => item.Id == next.Id);
            var prerequisiteSave = scenario.Contains("save");
            var pendingConfirm = scenario.Contains("confirm");
            if (prerequisiteSave) { window.slipEditor.Text = "submitted draft"; Dispatcher.UIThread.RunJobs(); }
            Task running = scenario.StartsWith("move") ? InvokeWorkflow(window, "MoveSlipAsync")
                : scenario.StartsWith("add") ? InvokeWorkflow(window, "AddSlipAsync", next.Id)
                : scenario.StartsWith("divider") ? InvokeWorkflow(window, "InsertDividerSlipAsync")
                : InvokeWorkflow(window, "DeleteSlipAsync", (Func<int, Task<bool>>)(_ =>
                {
                    if (!pendingConfirm) return Task.FromResult(true);
                    confirming.SetResult();
                    return answer.Task;
                }));
            var reached = pendingConfirm ? confirming.Task : arrived.Task;
            await Task.WhenAny(running, reached).WaitAsync(TimeSpan.FromSeconds(5));
            if (running.IsCompleted) await running;
            Assert.True(reached.IsCompleted, $"Workflow stopped before its await: {window.statusText.Text}");
            Task? navigation = null;
            if (scenario.EndsWith("navigation")) navigation = window.ActivateRequestAsync(replacement.Id);
            else
            {
                window.slipEditor.Text = "later writing";
                Dispatcher.UIThread.RunJobs();
                if (!pendingConfirm) PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(project));
                Assert.Equal(first.Id, window.editorState.SlipId);
            }
            if (pendingConfirm) answer.SetResult(true);
            release.Set();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            if (navigation is not null) await navigation.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(pendingConfirm ? 0 : 1, commands.Count);
            Assert.All(commands, command => Assert.Equal(project.Id, command.ProjectId));
            Assert.False(WindowField<KastnMutationCoordinator>(window, "mutations").IsBusy);
            Assert.False(WindowField<KastnMutationCoordinator>(window, "mutations").IsPreparing(KastnMutationPreparation.Delete));
            Assert.False(WindowField<KastnMutationCoordinator>(window, "mutations").IsPreparing(KastnMutationPreparation.AddSlip));
            if (scenario.EndsWith("navigation"))
            {
                Assert.Equal(replacement.Id, controller.Current.Project?.Id);
                Assert.Equal("replacement", other.Text);
                Assert.Single(replacement.Buckets[0].Slips);
                Assert.Equal(prerequisiteSave ? "submitted draft" : "baseline", first.Text);
            }
            else
            {
                Assert.Equal(first.Id, window.editorState.SlipId);
                Assert.Equal("later writing", window.slipEditor.Text);
                Assert.True(window.editorState.IsDirty);
                if (!pendingConfirm)
                {
                    Assert.Equal("baseline", drafts.Draft?.BaselineText);
                    Assert.Equal("later writing", drafts.Draft?.DraftText);
                    Assert.Equal(scenario.StartsWith("move"), next.Slips.Contains(first));
                    Assert.Equal(scenario.StartsWith("delete"), first.DeletedAtUtc is not null);
                }
                else Assert.Null(first.DeletedAtUtc);
            }
        }
        finally
        {
            release.Set();
            answer.TrySetResult(false);
            if (window is not null) CloseWindow(window);
            Directory.Delete(directory, recursive: true);
        }
    }
}
