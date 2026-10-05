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
    public static IEnumerable<object[]> SlipBatchInterruptions =>
        from kind in new[] { "format", "move", "delete" }
        from change in new[] { "unchanged", "navigation", "selection", "conflict", "retire" }
        select new object[] { kind, change };

    [AvaloniaTheory]
    [MemberData(nameof(SlipBatchInterruptions))]
    public async Task SlipBatchesOwnBusyAndCapturedTargetsAndKeepNavigationSnapshots(string kind, string change)
    {
        var directory = Path.Combine(Path.GetTempPath(), "KastnMutationBatch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        using var release = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource();
        var commands = new ConcurrentQueue<ZetlCommandEnvelope>();
        try
        {
            var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "kastn-ui");
            var project = store.CreateProject("Batch source", ["Inbox", "Next"], "Inbox");
            var inbox = project.Buckets.First(b => b.Name == "Inbox");
            var next = project.Buckets.First(b => b.Name == "Next");
            var first = store.AddSlip(inbox, "first", "copy");
            var second = store.AddSlip(inbox, "second", "copy");
            var third = store.AddSlip(inbox, "third", "copy");
            var replacement = store.CreateProject("Replacement", ["Inbox"], "Inbox");
            var reused = store.AddSlip(replacement.Buckets[0], "replacement writing", "copy");
            reused.Id = second.Id;
            var pipe = $"kastn-mutation-batch-{Guid.NewGuid():N}";
            using var server = new ZetlIpcServer(new ZetlProjectService(store), pipe, log: null,
                dropResponseForTesting: command =>
                {
                    if (command.Kind is ZetlCommandKind.UpdateSlip or ZetlCommandKind.MoveSlip or ZetlCommandKind.DeleteSlip)
                    {
                        commands.Enqueue(command);
                        if (arrived.TrySetResult()) release.Wait(TimeSpan.FromSeconds(5));
                    }
                    return false;
                });
            server.Start();
            await using var controller = new KastnConnectionController(
                _ => throw new InvalidOperationException("Zetl already running."), pipe,
                TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(25));
            controller.Start(project.Id);
            await WaitForConditionAsync(() => controller.Current.Project?.Id == project.Id, "Source should load.");
            window = new MainWindow(controller, new KastnDraftStore(Path.Combine(directory, "draft.json")));
            window.Show();
            window.projectTree.SelectedItem = null;
            window.projectTree.SelectedItems!.Clear();
            foreach (var id in new[] { third.Id, second.Id, first.Id })
                window.projectTree.SelectedItems.Add(window.treeProjection.Find(id)!);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(window.editorState.SlipId);
            var destination = WindowField<ComboBox>(window, "moveBucketBox");
            destination.SelectedItem = destination.ItemsSource!.Cast<KastnBucketItem>().Single(item => item.Id == next.Id);
            Task Start() => kind switch
            {
                "format" => InvokeWorkflow(window, "AlignSlipsAsync", "center"),
                "move" => InvokeWorkflow(window, "MoveSlipAsync"),
                _ => InvokeWorkflow(window, "DeleteSlipAsync", (Func<int, Task<bool>>)(_ => Task.FromResult(true)))
            };
            var running = Start();
            await Task.WhenAny(running, arrived.Task).WaitAsync(TimeSpan.FromSeconds(5));
            if (running.IsCompleted) await running;
            Assert.True(arrived.Task.IsCompleted, $"Batch stopped before submission: {window.statusText.Text}");
            Assert.True(WindowField<KastnMutationCoordinator>(window, "mutations").IsBusy);
            await Start();
            Assert.Single(commands);
            await Task.Delay(170);
            Dispatcher.UIThread.RunJobs();
            Assert.True(WindowField<bool>(window, "savingVisual"));
            Task? navigating = null;
            if (change == "navigation")
            {
                navigating = window.ActivateRequestAsync(replacement.Id);
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(replacement));
                Assert.Equal(replacement.Id, WindowField<ZetlProjectSnapshot>(window, "currentProject").Id);
            }
            else if (change == "selection") window.projectTree.SelectedItem = window.treeProjection.Find(third.Id);
            else if (change == "retire") CloseWindow(window);
            else
            {
                if (change == "conflict") store.UpdateSlip(second, "remote second");
                // Explicit snapshots still reach the UI inside the batch; only
                // ordinary command/event refreshes are deferred by the controller.
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(project));
                Assert.Null(window.editorState.SlipId);
            }
            release.Set();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            if (navigating is not null) await navigating.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.False(WindowField<KastnMutationCoordinator>(window, "mutations").IsBusy);
            var complete = change is "unchanged" or "conflict";
            Assert.Equal(complete ? 3 : 1, commands.Count);
            Assert.All(commands, command => Assert.Equal(project.Id, command.ProjectId));
            Assert.Equal(complete ? new[] { first.Id, second.Id, third.Id } : new[] { first.Id }, commands.Select(c => c.TargetId));
            Assert.Equal("replacement writing", reused.Text);
            Assert.Null(reused.DeletedAtUtc);
            Assert.Equal(kind == "format" ? "center" : null, first.Align);
            Assert.Equal(kind == "move", next.Slips.Contains(first));
            Assert.Equal(kind == "delete", first.DeletedAtUtc is not null);
            if (change == "conflict")
            {
                Assert.Equal("remote second", second.Text);
                Assert.Null(second.DeletedAtUtc);
                Assert.Contains(second, inbox.Slips);
                Assert.Contains("1 failed", window.statusText.Text);
            }
            if (change == "selection") Assert.Equal(third.Id, window.editorState.SlipId);
            if (change == "navigation") Assert.Equal(replacement.Id, WindowField<ZetlProjectSnapshot>(window, "currentProject").Id);
            if (complete) Assert.False(WindowField<bool>(window, "savingVisual"));
        }
        finally
        {
            release.Set();
            if (window is not null) CloseWindow(window);
            Directory.Delete(directory, recursive: true);
        }
    }
}
