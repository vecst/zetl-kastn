using System.Text.Json;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public class KastnEditHistoryTests
{
    [Fact]
    public async Task UndoRedoWalksRecordedRevisionsAndNewEditsClearRedo()
    {
        using var h = new HistoryHarness();
        await h.Edit(h.FirstId, "one");
        await h.Edit(h.FirstId, "two");
        Assert.Equal(2, h.History.UndoCount);
        Assert.Equal(KastnHistoryStepStatus.Completed, (await h.Step()).Status);
        Assert.Equal("one", h.Slip(h.FirstId).Text);
        await h.Step();
        Assert.Equal("baseline", h.Slip(h.FirstId).Text);
        Assert.Equal(2, h.History.RedoCount);
        await h.Step(redo: true);
        await h.Step(redo: true);
        Assert.Equal("two", h.Slip(h.FirstId).Text);
        await h.Step();
        await h.Edit(h.FirstId, "new action");
        Assert.Equal(0, h.History.RedoCount);
        Assert.Equal(0, h.Deferrals);
    }

    [Fact]
    public async Task NestedGesturesCoalesceMoveAndContentIntoOneInverse()
    {
        using var h = new HistoryHarness();
        using (h.History.BeginGesture("Move and edit"))
        {
            await h.Mutate(ZetlCommandKind.MoveSlip, new MoveSlipCommand { DestinationBucketId = h.NextId }, h.FirstId);
            using (h.History.BeginGesture("Nested edit")) await h.Edit(h.FirstId, "changed");
        }
        Assert.Equal(1, h.History.UndoCount);
        Assert.Equal("Move and edit", h.History.Peek()!.Description);
        Assert.Single(h.History.Peek()!.Operations);
        await h.Step();
        Assert.Equal(h.InboxId, h.Slip(h.FirstId).BucketId);
        Assert.Equal("baseline", h.Slip(h.FirstId).Text);
        await h.Step(redo: true);
        Assert.Equal(h.NextId, h.Slip(h.FirstId).BucketId);
        Assert.Equal("changed", h.Slip(h.FirstId).Text);
    }

    [Fact]
    public async Task UnrelatedAsyncFlowDoesNotJoinAnOpenGesture()
    {
        using var h = new HistoryHarness();
        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        async Task GroupedEdit()
        {
            using var gesture = h.History.BeginGesture("Grouped edit");
            await h.Edit(h.FirstId, "first change");
            started.SetResult();
            await finish.Task;
            await h.Edit(h.FirstId, "second change");
        }
        var group = GroupedEdit();
        await started.Task;
        await h.Edit(h.SecondId, "unrelated change");
        finish.SetResult();
        await group;
        Assert.Equal(2, h.History.UndoCount);
        Assert.Equal(h.FirstId, Assert.Single(h.History.Peek()!.Operations).SlipId);
        await h.Step();
        Assert.Equal("baseline", h.Slip(h.FirstId).Text);
        Assert.Equal("unrelated change", h.Slip(h.SecondId).Text);
        await h.Step();
        Assert.Equal("second baseline", h.Slip(h.SecondId).Text);
    }

    [Fact]
    public async Task GestureScopesBelongToTheirHistoryOwner()
    {
        using var first = new HistoryHarness();
        using var second = new HistoryHarness();
        using (first.History.BeginGesture("First window"))
        {
            using (second.History.BeginGesture("Second window"))
                await second.Edit(second.FirstId, "second edit");
            await first.Edit(first.FirstId, "first edit");
        }
        Assert.Equal("First window", first.History.Peek()!.Description);
        Assert.Equal("Second window", second.History.Peek()!.Description);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedCompoundCanRepairThenRetry(bool unknown)
    {
        using var h = new HistoryHarness();
        using (h.History.BeginGesture("Edit both"))
        {
            await h.Edit(h.FirstId, "first changed");
            await h.Edit(h.SecondId, "second changed");
        }
        h.Handler = command =>
        {
            if (command.TargetId != h.SecondId) return Task.FromResult(h.ExecuteCore(command));
            if (!unknown) return Task.FromResult(Failure(command));
            h.ExecuteCore(command);
            throw new ZetlCommandOutcomeUnknownException(command.CommandId, "lost response", new IOException());
        };
        var result = await h.Step();
        Assert.Equal(unknown ? KastnHistoryStepStatus.OutcomeUnknown : KastnHistoryStepStatus.Interrupted, result.Status);
        Assert.True(result.HasRepair);
        Assert.Equal(2, h.History.UndoCount);
        Assert.Equal(0, h.History.RedoCount);
        Assert.True(h.History.Peek()!.IsRepair);
        h.Handler = null;
        await h.Step();
        Assert.Equal("first changed", h.Slip(h.FirstId).Text);
        Assert.Equal("second changed", h.Slip(h.SecondId).Text);
        Assert.Equal(1, h.History.UndoCount);
        Assert.Equal(0, h.History.RedoCount); // Repair is not a new user action.
        await h.Step();
        Assert.Equal("baseline", h.Slip(h.FirstId).Text);
        Assert.Equal("second baseline", h.Slip(h.SecondId).Text);
        Assert.Equal(1, h.History.RedoCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConflictDecisionCanKeepOrApplySlipAndBucketChanges(bool bucket, bool apply)
    {
        using var h = new HistoryHarness();
        if (bucket)
        {
            await h.Mutate(ZetlCommandKind.UpdateBucket, new UpdateBucketCommand { Name = "renamed" }, h.InboxId, bucket: true);
            h.Service.Execute(h.Command(ZetlCommandKind.UpdateBucket, new UpdateBucketCommand { Name = "remote" }, h.InboxId, bucket: true));
        }
        else
        {
            await h.Edit(h.FirstId, "changed");
            h.Service.Execute(h.Command(ZetlCommandKind.UpdateSlip, new UpdateSlipCommand { Text = "remote" }, h.FirstId));
        }
        h.Reload();
        var decisions = 0;
        var result = await h.History.StepAsync(false, conflict =>
        {
            decisions++;
            Assert.Contains("remote", conflict.CurrentText);
            return Task.FromResult(apply);
        });
        Assert.Equal(1, decisions);
        Assert.Equal(apply ? 0 : 1, result.Kept);
        Assert.Equal(apply ? 1 : 0, h.History.RedoCount);
        Assert.Equal(apply ? bucket ? "Inbox" : "baseline" : "remote",
            bucket ? h.Current.Buckets.First(item => item.Id == h.InboxId).Name : h.Slip(h.FirstId).Text);
        if (apply)
        {
            await h.Step(redo: true);
            Assert.Equal("remote", bucket ? h.Current.Buckets.First(item => item.Id == h.InboxId).Name : h.Slip(h.FirstId).Text);
        }
    }

    [Fact]
    public async Task BestEffortPositionFailureDoesNotAbortAConfirmedMove()
    {
        using var h = new HistoryHarness();
        await h.Mutate(ZetlCommandKind.MoveSlip, new MoveSlipCommand { DestinationBucketId = h.NextId }, h.FirstId);
        h.Handler = command => Task.FromResult(command.Kind == ZetlCommandKind.ReorderSlip
            ? Failure(command) with { Status = ZetlResponseStatus.ValidationError } : h.ExecuteCore(command));
        var result = await h.Step();
        Assert.Equal(KastnHistoryStepStatus.Completed, result.Status);
        Assert.Equal(h.InboxId, h.Slip(h.FirstId).BucketId);
        Assert.Equal(1, h.History.RedoCount);
    }

    [Theory]
    [InlineData("server")]
    [InlineData("project")]
    [InlineData("transient")]
    public async Task DelayedRecordingRespectsHistoryGeneration(string interruption)
    {
        using var h = new HistoryHarness();
        var reply = new TaskCompletionSource<ZetlResponseEnvelope>();
        h.Handler = command => { h.DelayedResponse = h.ExecuteCore(command); return reply.Task; };
        var editing = h.Edit(h.FirstId, "changed");
        h.Interrupt(interruption);
        reply.SetResult(h.DelayedResponse!);
        await editing;
        Assert.Equal(interruption == "transient" ? 1 : 0, h.History.UndoCount);
    }

    [Theory]
    [InlineData("server")]
    [InlineData("project")]
    public async Task InvalidatedInverseStopsUnsentCommands(string interruption)
    {
        using var h = new HistoryHarness();
        using (h.History.BeginGesture("Move and edit"))
        {
            await h.Mutate(ZetlCommandKind.MoveSlip, new MoveSlipCommand { DestinationBucketId = h.NextId }, h.FirstId);
            await h.Edit(h.FirstId, "changed");
        }
        h.Commands.Clear();
        h.Handler = command =>
        {
            var response = h.ExecuteCore(command);
            h.Interrupt(interruption);
            return Task.FromResult(response);
        };
        Assert.Equal(KastnHistoryStepStatus.Invalidated, (await h.Step()).Status);
        Assert.Single(h.Commands);
        Assert.Equal("changed", h.Slip(h.FirstId).Text);
        Assert.Equal(0, h.History.UndoCount);
        Assert.Equal(0, h.History.RedoCount);
        Assert.False(h.History.IsStepping);
    }

    [Fact]
    public async Task UnknownOutcomeWithoutRefreshRetainsOriginalEntry()
    {
        using var h = new HistoryHarness();
        await h.Edit(h.FirstId, "changed");
        var entry = h.History.Peek();
        h.Handler = command => throw new ZetlCommandOutcomeUnknownException(command.CommandId, "lost response", new IOException());
        h.RefreshHandler = () => Task.FromResult<KastnSessionSnapshot?>(null);
        var result = await h.Step();
        Assert.Equal(KastnHistoryStepStatus.OutcomeUnknown, result.Status);
        Assert.False(result.HasRepair);
        Assert.Equal(entry!.EntryId, h.History.Peek()!.EntryId);
        Assert.Equal(0, h.History.RedoCount);
    }

    [Fact]
    public async Task RestartDiscoveredDuringReconciliationClearsBothStacks()
    {
        using var h = new HistoryHarness();
        await h.Edit(h.FirstId, "changed");
        h.RefreshHandler = () => Task.FromResult<KastnSessionSnapshot?>(h.Session() with { ServerInstanceId = "restarted" });
        Assert.Equal(KastnHistoryStepStatus.Invalidated, (await h.Step()).Status);
        Assert.Equal(0, h.History.UndoCount);
        Assert.Equal(0, h.History.RedoCount);
    }

    [Fact]
    public async Task ReportedServerRestartInvalidatesHistoryEvenWithoutRefresh()
    {
        using var h = new HistoryHarness();
        await h.Edit(h.FirstId, "changed");
        h.Handler = command => throw new ZetlCommandOutcomeUnknownException(command.CommandId,
            "server changed", new IOException(), serverInstanceChanged: true);
        h.RefreshHandler = () => Task.FromResult<KastnSessionSnapshot?>(null);
        Assert.Equal(KastnHistoryStepStatus.Invalidated, (await h.Step()).Status);
        Assert.Equal(0, h.History.UndoCount);
        Assert.Equal(0, h.History.RedoCount);
    }

    [Fact]
    public async Task RetiredGestureCannotRecordAfterReturningToTheSameProject()
    {
        using var h = new HistoryHarness();
        using (h.History.BeginGesture("Old gesture"))
        {
            await h.Edit(h.FirstId, "old change");
            h.Interrupt("project");
            await h.Edit(h.FirstId, "late change");
        }
        Assert.Equal(0, h.History.UndoCount);
        await h.Edit(h.SecondId, "new action");
        Assert.Equal(1, h.History.UndoCount);
        Assert.Equal(h.SecondId, Assert.Single(h.History.Peek()!.Operations).SlipId);
    }

    [Fact]
    public async Task BusyOwnerRejectsOverlappingStepsAndOrdinaryMutations()
    {
        using var h = new HistoryHarness();
        await h.Edit(h.FirstId, "changed");
        var reply = new TaskCompletionSource<ZetlResponseEnvelope>();
        h.Handler = command => { h.DelayedResponse = h.ExecuteCore(command); return reply.Task; };
        var undo = h.Step();
        Assert.True(h.History.IsStepping);
        Assert.Equal(KastnHistoryStepStatus.Busy, (await h.Step(redo: true)).Status);
        var commands = h.Commands.Count;
        var blocked = await h.Edit(h.SecondId, "blocked");
        Assert.Equal("edit_history_busy", blocked.Error?.Code);
        Assert.Equal(commands, h.Commands.Count);
        reply.SetResult(h.DelayedResponse!);
        await undo;
        Assert.False(h.History.IsStepping);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("project")]
    [InlineData("target")]
    [InlineData("payload")]
    [InlineData("malformed")]
    [InlineData("revision")]
    public async Task UnconfirmedMutationCannotCreateAnUndoEntry(string mismatch)
    {
        using var h = new HistoryHarness();
        h.Handler = command =>
        {
            var response = h.ExecuteCore(command);
            var saved = h.Slip(h.FirstId);
            return Task.FromResult(mismatch switch
            {
                "command" => response with { CommandId = "other" },
                "project" => response with { ProjectId = "other" },
                "target" => response with { Payload = ZetlProtocolJson.ToElement(saved with { Id = "other" }) },
                "payload" => response with { Payload = null },
                "malformed" => response with { Payload = ZetlProtocolJson.ToElement("not a snapshot") },
                _ => response with { Payload = ZetlProtocolJson.ToElement(saved with { Revision = command.ExpectedTargetRevision!.Value }) }
            });
        };
        await h.Edit(h.FirstId, "changed");
        Assert.Equal(0, h.History.UndoCount);
        Assert.Equal(0, h.History.RedoCount);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("project")]
    [InlineData("target")]
    [InlineData("payload")]
    [InlineData("malformed")]
    [InlineData("revision")]
    public async Task UnconfirmedInverseRetainsEntryAndReconcilesActualState(string mismatch)
    {
        using var h = new HistoryHarness();
        await h.Edit(h.FirstId, "changed");
        h.Handler = command =>
        {
            var response = h.ExecuteCore(command);
            var saved = response.Payload!.Value.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)!;
            return Task.FromResult(mismatch switch
            {
                "command" => response with { CommandId = "other" },
                "project" => response with { ProjectId = "other" },
                "target" => response with { Payload = ZetlProtocolJson.ToElement(saved with { Id = "other" }) },
                "payload" => response with { Payload = null },
                "malformed" => response with { Payload = ZetlProtocolJson.ToElement("not a snapshot") },
                _ => response with { Payload = ZetlProtocolJson.ToElement(saved with { Revision = command.ExpectedTargetRevision!.Value }) }
            });
        };
        var result = await h.Step();
        Assert.Equal(KastnHistoryStepStatus.OutcomeUnknown, result.Status);
        Assert.True(result.HasRepair);
        Assert.Equal(2, h.History.UndoCount);
        Assert.Equal(0, h.History.RedoCount);
    }

    private static ZetlResponseEnvelope Failure(ZetlCommandEnvelope command) => new()
    {
        CommandId = command.CommandId, ProjectId = command.ProjectId, Status = ZetlResponseStatus.Failure
    };

    private sealed class HistoryHarness : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "KastnHistory", Guid.NewGuid().ToString("N"));
        public readonly ZetlProject Project;
        public readonly ZetlProjectService Service;
        public readonly KastnEditHistory History;
        public readonly string FirstId, SecondId, InboxId, NextId;
        public ZetlProjectSnapshot Current;
        public string ServerId = "server-one";
        public Func<ZetlCommandEnvelope, Task<ZetlResponseEnvelope>>? Handler;
        public Func<Task<KastnSessionSnapshot?>>? RefreshHandler;
        public ZetlResponseEnvelope? DelayedResponse;
        public List<ZetlCommandEnvelope> Commands { get; } = [];
        public int Deferrals;

        public HistoryHarness()
        {
            Directory.CreateDirectory(directory);
            var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "history-test");
            Project = store.CreateProject("History", ["Inbox", "Next"], "Inbox");
            var inbox = Project.Buckets.First(item => item.Name == "Inbox");
            InboxId = inbox.Id;
            NextId = Project.Buckets.First(item => item.Name == "Next").Id;
            FirstId = store.AddSlip(inbox, "baseline", "copy").Id;
            SecondId = store.AddSlip(inbox, "second baseline", "copy").Id;
            Service = new ZetlProjectService(store);
            Current = ZetlProjectSnapshotMapper.ToSnapshot(Project);
            History = new KastnEditHistory(() => Current, command =>
            {
                Commands.Add(command);
                return Handler?.Invoke(command) ?? Task.FromResult(ExecuteCore(command));
            }, () => { if (RefreshHandler is not null) return RefreshHandler(); Reload(); return Task.FromResult<KastnSessionSnapshot?>(Session()); },
                () => { Deferrals++; return new RefreshScope(this); });
            History.ObserveSession(Session());
        }

        public KastnSessionSnapshot Session() => new(KastnConnectionState.Online, "Connected", [], Current) { ServerInstanceId = ServerId };
        public void Reload() => Current = ZetlProjectSnapshotMapper.ToSnapshot(Project);
        public ZetlSlipSnapshot Slip(string id) => Current.Slips.First(slip => slip.Id == id);
        public ZetlResponseEnvelope ExecuteCore(ZetlCommandEnvelope command)
        {
            var response = Service.Execute(command);
            Reload();
            return response;
        }
        public ZetlCommandEnvelope Command(ZetlCommandKind kind, object payload, string id, bool bucket = false) =>
            ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), kind, payload, Current.Id, id,
                bucket ? Current.Buckets.First(item => item.Id == id).Revision : Slip(id).Revision);
        public Task<ZetlResponseEnvelope> Mutate(ZetlCommandKind kind, object payload, string id, bool bucket = false) =>
            History.ExecuteMutationAsync(Command(kind, payload, id, bucket));
        public Task<ZetlResponseEnvelope> Edit(string id, string text) => Mutate(ZetlCommandKind.UpdateSlip, new UpdateSlipCommand { Text = text }, id);
        public Task<KastnHistoryStepResult> Step(bool redo = false) => History.StepAsync(redo,
            _ => throw new InvalidOperationException("Unexpected history conflict."));
        public void Interrupt(string interruption)
        {
            if (interruption == "server") ServerId = "server-two";
            if (interruption == "project")
            {
                var original = Current;
                Current = original with { Id = "other-project" };
                History.ObserveSession(Session());
                Current = original;
            }
            if (interruption == "transient") History.ObserveSession(Session() with { ConnectionState = KastnConnectionState.Connecting });
            History.ObserveSession(Session());
        }
        public void Dispose() => Directory.Delete(directory, recursive: true);
        private sealed class RefreshScope(HistoryHarness owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { owner.Deferrals--; return ValueTask.CompletedTask; }
        }
    }
}
