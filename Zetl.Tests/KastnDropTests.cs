using System.Text.Json;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public class KastnDropTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EquivalentTreeEdgesUseOneInsertionMarker(bool multiple)
    {
        var index = WithSlips(Index(), [Slip("one", "a"), Slip("two", "a"), Slip("three", "a"), Slip("four", "a")]);
        var ids = multiple ? new[] { "three", "four" } : ["three"];
        var belowOne = KastnDropPlanner.Plan(index, "three", true, ids,
            new(KastnDropTargetKind.Slip, "one", KastnDropEdge.After))!;
        var aboveTwo = KastnDropPlanner.Plan(index, "three", true, ids,
            new(KastnDropTargetKind.Slip, "two", KastnDropEdge.Before))!;
        Assert.Equal(aboveTwo.DestinationBucketId, belowOne.DestinationBucketId);
        Assert.Equal(aboveTwo.BeforeSlipId, belowOne.BeforeSlipId);
        Assert.Equal(aboveTwo.MarkerId, belowOne.MarkerId);
        Assert.Equal(aboveTwo.MarkerEdge, belowOne.MarkerEdge);
        Assert.Equal("two", belowOne.MarkerId);
        Assert.Equal(KastnDropEdge.Before, belowOne.MarkerEdge);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TreeAndBoardCaptureDocumentOrderAndTheSameInsertionSlot(bool board)
    {
        var index = Index();
        var plan = KastnDropPlanner.Plan(index, "two", true, ["two", "one", "two"], board
            ? new(KastnDropTargetKind.BoardSlot, "b", BeforeSlipId: "anchor", MarkerId: "anchor")
            : new(KastnDropTargetKind.Slip, "anchor", KastnDropEdge.Before));
        Assert.NotNull(plan);
        Assert.Equal(new[] { "one", "two" }, plan.SlipIds);
        Assert.Equal("b", plan.DestinationBucketId);
        Assert.Equal("anchor", plan.BeforeSlipId);
        Assert.Equal("anchor", plan.MarkerId);
    }

    [Fact]
    public void AfterDropSkipsDraggedAnchorsAndSuppressesAnUnchangedOrder()
    {
        var index = WithSlips(Index(), [Slip("one", "a"), Slip("anchor", "a"), Slip("two", "a"), Slip("tail", "a")]);
        var plan = KastnDropPlanner.Plan(index, "one", true, ["two", "one"],
            new(KastnDropTargetKind.Slip, "anchor", KastnDropEdge.After));
        Assert.NotNull(plan);
        Assert.Equal("tail", plan.BeforeSlipId);
        Assert.Equal("tail", plan.MarkerId);
        Assert.Equal(KastnDropEdge.Before, plan.MarkerEdge);
        Assert.Null(KastnDropPlanner.Plan(index, "two", true, ["two"],
            new(KastnDropTargetKind.Slip, "anchor", KastnDropEdge.After)));
        Assert.Null(KastnDropPlanner.Plan(index, "tail", true, ["tail"], new(KastnDropTargetKind.Bucket, "a")));
    }

    [Theory]
    [InlineData("self")]
    [InlineData("descendant")]
    [InlineData("deleted")]
    [InlineData("cycle")]
    [InlineData("missing")]
    public void BucketNestingRejectsUnsafeAncestry(string target)
    {
        var index = Index();
        var id = target switch { "self" => "a", "descendant" => "child", "cycle" => "cycle-one", _ => target };
        Assert.Null(KastnDropPlanner.Plan(index, "a", false, [], new(KastnDropTargetKind.Bucket, id)));
        Assert.True(index.IsDescendant("child", "a"));
        Assert.False(index.IsDescendant("cycle-one", "a"));
    }

    [Fact]
    public void BucketEdgesPromoteAndNestWithBucketMarkers()
    {
        var index = Index();
        var reorder = KastnDropPlanner.Plan(index, "child", false, [], new(KastnDropTargetKind.Bucket, "b", KastnDropEdge.Before))!;
        Assert.Equal(KastnDropAction.BucketReorder, reorder.Action);
        Assert.Null(reorder.NewParentBucketId);
        Assert.Equal("b", reorder.BeforeBucketId);
        var nested = KastnDropPlanner.Plan(index, "a", false, [], new(KastnDropTargetKind.Slip, "anchor", KastnDropEdge.Before))!;
        Assert.Equal(KastnDropAction.BucketReparent, nested.Action);
        Assert.Equal("b", nested.NewParentBucketId);
        Assert.Equal("b", nested.MarkerId);
        Assert.Equal(KastnDropEdge.None, nested.MarkerEdge);
        var promoted = KastnDropPlanner.Plan(index, "child", false, [], new(KastnDropTargetKind.Empty))!;
        Assert.Null(promoted.NewParentBucketId);
        Assert.Null(promoted.BeforeBucketId);
        Assert.Null(KastnDropPlanner.Plan(index, "child", false, [], new(KastnDropTargetKind.Bucket, "a")));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("source")]
    [InlineData("destination")]
    [InlineData("deleted-destination")]
    [InlineData("anchor")]
    [InlineData("moved-anchor")]
    public void CapturedDropIsRevalidatedAgainstTheFreshSnapshot(string change)
    {
        var index = Index();
        var plan = KastnDropPlanner.Plan(index, "one", true, ["two"], new(KastnDropTargetKind.Slip, "anchor", KastnDropEdge.Before))!;
        var current = index.Project;
        var changed = change switch
        {
            "project" => current with { Id = "other" },
            "source" => current with { Slips = current.Slips.Where(s => s.Id != "one").ToArray() },
            "destination" => current with { Buckets = current.Buckets.Where(b => b.Id != "b").ToArray() },
            "deleted-destination" => current with { Buckets = current.Buckets.Select(b => b.Id == "b" ? b with { Settings = new() { Kind = "Deleted" } } : b).ToArray() },
            "anchor" => current with { Slips = current.Slips.Where(s => s.Id != "anchor").ToArray() },
            _ => current with { Slips = current.Slips.Select(s => s.Id == "anchor" ? s with { BucketId = "a" } : s).ToArray() }
        };
        Assert.False(KastnDropPlanner.IsValid(new(changed), plan));
        Assert.Throws<InvalidOperationException>(() => new KastnMoveOperation(new(changed), plan));
    }

    [Fact]
    public void DeletedSourcesAndDraggedTargetsAreRejected()
    {
        var index = Index();
        Assert.Null(KastnDropPlanner.Plan(index, "trashed", true, [], new(KastnDropTargetKind.Bucket, "b")));
        Assert.Null(KastnDropPlanner.Plan(index, "deleted", false, [], new(KastnDropTargetKind.Empty)));
        Assert.Null(KastnDropPlanner.Plan(index, "one", true, ["two"], new(KastnDropTargetKind.Slip, "two", KastnDropEdge.Before)));
        Assert.Null(KastnDropPlanner.Plan(index, "one", true, [], new(KastnDropTargetKind.BoardSlot, "b", BeforeSlipId: "one")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleSlipsMoveInOrderAndUndoAsOneGesture(bool append)
    {
        using var h = new MoveHarness();
        var before = h.Current.Slips.Select(s => (s.Id, s.BucketId)).ToArray();
        var plan = h.Plan(append);
        KastnMoveResult result;
        using (h.History.BeginGesture("Move slips"))
            result = await new KastnMoveOperation(new(h.Current), plan).ExecuteAsync(h.Execute, () => true);
        Assert.Equal(KastnMoveStatus.Completed, result.Status);
        Assert.Equal(2, result.CompletedItems);
        Assert.Equal(append ? 2 : 4, result.ConfirmedCommands);
        Assert.Equal(append ? new[] { h.AnchorId, h.FirstId, h.SecondId } : new[] { h.FirstId, h.SecondId, h.AnchorId },
            h.Current.Slips.Where(s => s.BucketId == h.NextId).Select(s => s.Id));
        Assert.Equal(1, h.History.UndoCount);
        Assert.Equal(2, h.History.Peek()!.Operations.Count);
        var after = h.Current.Slips.Select(s => (s.Id, s.BucketId)).ToArray();
        Assert.Equal(KastnHistoryStepStatus.Completed, (await h.Step()).Status);
        Assert.Equal(before, h.Current.Slips.Select(s => (s.Id, s.BucketId)));
        await h.Step(true);
        Assert.Equal(after, h.Current.Slips.Select(s => (s.Id, s.BucketId)));
        Assert.All(h.Commands.Where(c => c.Kind == ZetlCommandKind.ReorderSlip).Take(append ? 0 : 2),
            c => Assert.Equal(2, c.ExpectedTargetRevision));
    }

    [Fact]
    public async Task MixedBucketSourcesKeepTheirCapturedOrderWhenAppending()
    {
        using var h = new MoveHarness();
        var plan = KastnDropPlanner.Plan(new(h.Current), h.FirstId, true, [h.AnchorId, h.SecondId],
            new(KastnDropTargetKind.Bucket, h.NextId))!;
        var result = await new KastnMoveOperation(new(h.Current), plan).ExecuteAsync(h.Execute, () => true);
        Assert.Equal(KastnMoveStatus.Completed, result.Status);
        Assert.Equal(3, result.CompletedItems);
        Assert.Equal(new[] { h.FirstId, h.SecondId, h.AnchorId }, h.Current.Slips.Where(s => s.BucketId == h.NextId).Select(s => s.Id));
    }

    [Fact]
    public async Task SameBucketGroupReordersWithoutSendingMoveCommands()
    {
        using var h = new MoveHarness();
        var plan = KastnDropPlanner.Plan(new(h.Current), h.SecondId, true, [],
            new(KastnDropTargetKind.Slip, h.FirstId, KastnDropEdge.Before))!;
        var result = await new KastnMoveOperation(new(h.Current), plan).ExecuteAsync(h.Execute, () => true);
        Assert.Equal(KastnMoveStatus.Completed, result.Status);
        Assert.Equal(ZetlCommandKind.ReorderSlip, Assert.Single(h.Commands).Kind);
        Assert.Equal(new[] { h.SecondId, h.FirstId }, h.Current.Slips.Where(s => s.BucketId == h.InboxId).Select(s => s.Id));
    }

    [Fact]
    public async Task BucketReparentAndReorderUseFreshPropertiesAndThreadRevisions()
    {
        using var h = new MoveHarness();
        var plan = KastnDropPlanner.Plan(new(h.Current), h.NextId, false, [],
            new(KastnDropTargetKind.Bucket, h.ChildId, KastnDropEdge.Before))!;
        var bucket = h.Current.Buckets.Single(b => b.Id == h.NextId);
        h.Service.Execute(ZetlCommandEnvelope.Create("rename", ZetlCommandKind.UpdateBucket,
            new UpdateBucketCommand { Name = "fresh name", RenderKind = "group", Settings = new() { DefaultStartingText = "fresh default" } },
            h.Current.Id, bucket.Id, bucket.Revision));
        h.Reload();
        using (h.History.BeginGesture("Move bucket"))
        {
            var result = await new KastnMoveOperation(new(h.Current), plan).ExecuteAsync(h.Execute, () => true);
            Assert.Equal(KastnMoveStatus.Completed, result.Status);
            Assert.Equal(2, result.ConfirmedCommands);
        }
        Assert.Equal(new[] { ZetlCommandKind.UpdateBucket, ZetlCommandKind.ReorderBucket }, h.Commands.Select(c => c.Kind));
        Assert.Equal(new long?[] { 2, 3 }, h.Commands.Select(c => c.ExpectedTargetRevision));
        var moved = h.Current.Buckets.Single(b => b.Id == h.NextId);
        Assert.Equal(h.InboxId, moved.ParentBucketId);
        Assert.Equal("fresh name", moved.Name);
        Assert.Equal("fresh default", moved.Settings.DefaultStartingText);
        Assert.Equal("group", moved.RenderKind);
        Assert.Equal(new[] { h.NextId, h.ChildId }, h.Current.Buckets.Where(b => b.ParentBucketId == h.InboxId).Select(b => b.Id));
        Assert.Equal(1, h.History.UndoCount);
        await h.Step();
        Assert.Null(h.Current.Buckets.Single(b => b.Id == h.NextId).ParentBucketId);
        await h.Step(true);
        Assert.Equal(h.InboxId, h.Current.Buckets.Single(b => b.Id == h.NextId).ParentBucketId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReorderStopsTheGroupAndLeavesTheConfirmedMoveUndoable(bool conflict)
    {
        using var h = new MoveHarness();
        h.Handler = command => Task.FromResult(command.Kind == ZetlCommandKind.ReorderSlip
            ? new ZetlResponseEnvelope { CommandId = command.CommandId, ProjectId = command.ProjectId,
                Status = conflict ? ZetlResponseStatus.Conflict : ZetlResponseStatus.ValidationError }
            : h.ExecuteCore(command));
        KastnMoveResult result;
        using (h.History.BeginGesture("Move slips"))
            result = await new KastnMoveOperation(new(h.Current), h.Plan()).ExecuteAsync(h.Execute, () => true);
        Assert.Equal(conflict ? KastnMoveStatus.Conflict : KastnMoveStatus.Failed, result.Status);
        Assert.Equal(0, result.CompletedItems);
        Assert.Equal(1, result.ConfirmedCommands);
        Assert.Equal(2, h.Commands.Count);
        Assert.Equal(h.InboxId, h.Current.Slips.Single(s => s.Id == h.SecondId).BucketId);
        Assert.Single(h.History.Peek()!.Operations);
        h.Handler = null;
        await h.Step();
        Assert.Equal(new[] { h.FirstId, h.SecondId }, h.Current.Slips.Where(s => s.BucketId == h.InboxId).Select(s => s.Id));
    }

    [Theory]
    [InlineData("command")]
    [InlineData("project")]
    [InlineData("target")]
    [InlineData("revision")]
    [InlineData("destination")]
    [InlineData("missing")]
    [InlineData("malformed")]
    public async Task UnconfirmedSuccessNeverSuppliesARevisionForTheNextCommand(string mismatch)
    {
        using var h = new MoveHarness();
        h.Handler = command =>
        {
            var response = h.ExecuteCore(command);
            var slip = response.Payload!.Value.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)!;
            return Task.FromResult(mismatch switch
            {
                "command" => response with { CommandId = "other" },
                "project" => response with { ProjectId = "other" },
                "target" => response with { Payload = ZetlProtocolJson.ToElement(slip with { Id = "other" }) },
                "revision" => response with { Payload = ZetlProtocolJson.ToElement(slip with { Revision = slip.Revision + 1 }) },
                "destination" => response with { Payload = ZetlProtocolJson.ToElement(slip with { BucketId = h.InboxId }) },
                "missing" => response with { Payload = null },
                _ => response with { Payload = ZetlProtocolJson.ToElement("not a snapshot") }
            });
        };
        var result = await new KastnMoveOperation(new(h.Current), h.Plan()).ExecuteAsync(h.Execute, () => true);
        Assert.Equal(KastnMoveStatus.OutcomeUnknown, result.Status);
        Assert.Equal(0, result.ConfirmedCommands);
        Assert.Single(h.Commands);
    }

    [Theory]
    [InlineData("guard")]
    [InlineData("offline")]
    [InlineData("cancel")]
    [InlineData("unknown")]
    public async Task InterruptionReportsConfirmedWorkAndStopsUnsentTargets(string interruption)
    {
        using var h = new MoveHarness();
        h.Handler = command =>
        {
            if (command.Kind == ZetlCommandKind.MoveSlip) return Task.FromResult(h.ExecuteCore(command));
            throw interruption switch
            {
                "unknown" => new ZetlCommandOutcomeUnknownException(command.CommandId, "lost response", new IOException()),
                "cancel" => new OperationCanceledException(),
                _ => new IOException("Disconnected")
            };
        };
        var result = await new KastnMoveOperation(new(h.Current), h.Plan()).ExecuteAsync(h.Execute,
            () => interruption != "guard" || h.Commands.Count == 0);
        Assert.Equal(interruption == "unknown" ? KastnMoveStatus.OutcomeUnknown : KastnMoveStatus.Interrupted, result.Status);
        Assert.Equal(1, result.ConfirmedCommands);
        Assert.Equal(0, result.CompletedItems);
        Assert.Equal(interruption == "guard" ? 1 : 2, h.Commands.Count);
        Assert.Equal(h.InboxId, h.Current.Slips.Single(s => s.Id == h.SecondId).BucketId);
    }

    private static KastnProjectIndex Index() => new(new ZetlProjectSnapshot
    {
        Id = "project", Name = "Drops", MetadataRevision = 1, ChangeSequence = 1,
        Buckets = [Bucket("a"), Bucket("b"), Bucket("child", "a"),
            Bucket("deleted") with { Settings = new() { Kind = "Deleted" } },
            Bucket("cycle-one", "cycle-two"), Bucket("cycle-two", "cycle-one")],
        Slips = [Slip("one", "a"), Slip("two", "a"), Slip("anchor", "b"), Slip("trashed", "deleted")]
    });
    private static KastnProjectIndex WithSlips(KastnProjectIndex index, IReadOnlyList<ZetlSlipSnapshot> slips) => new(index.Project with { Slips = slips });
    private static ZetlBucketSnapshot Bucket(string id, string? parent = null) => new() { Id = id, Name = id, Revision = 1, ParentBucketId = parent };
    private static ZetlSlipSnapshot Slip(string id, string bucket) => new()
    {
        Id = id, BucketId = bucket, Revision = 1, Text = id, Type = ZetlSlipType.Text,
        Source = "copy", CapturedAtUtc = DateTimeOffset.UtcNow
    };

    private sealed class MoveHarness : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "KastnMoves", Guid.NewGuid().ToString("N"));
        public readonly ZetlProject Project;
        public readonly ZetlProjectService Service;
        public readonly KastnEditHistory History;
        public readonly string InboxId, NextId, ChildId, FirstId, SecondId, AnchorId;
        public ZetlProjectSnapshot Current;
        public List<ZetlCommandEnvelope> Commands { get; } = [];
        public Func<ZetlCommandEnvelope, Task<ZetlResponseEnvelope>>? Handler;

        public MoveHarness()
        {
            Directory.CreateDirectory(directory);
            var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "move-test");
            Project = store.CreateProject("Moves", ["Inbox", "Next"], "Inbox");
            var inbox = Project.Buckets.First(b => b.Name == "Inbox");
            var next = Project.Buckets.First(b => b.Name == "Next");
            InboxId = inbox.Id;
            NextId = next.Id;
            FirstId = store.AddSlip(inbox, "one", "copy").Id;
            SecondId = store.AddSlip(inbox, "two", "copy").Id;
            AnchorId = store.AddSlip(next, "anchor", "copy").Id;
            Service = new(store);
            var childResponse = Service.Execute(ZetlCommandEnvelope.Create("child", ZetlCommandKind.AddBucket,
                new AddBucketCommand { Name = "Child", ParentBucketId = InboxId }, Project.Id));
            ChildId = childResponse.Payload!.Value.Deserialize<ZetlBucketSnapshot>(ZetlProtocolJson.Options)!.Id;
            Current = ZetlProjectSnapshotMapper.ToSnapshot(Project);
            History = new(() => Current, command =>
            {
                Commands.Add(command);
                return Handler?.Invoke(command) ?? Task.FromResult(ExecuteCore(command));
            }, () => { Reload(); return Task.FromResult<KastnSessionSnapshot?>(Session()); }, () => new RefreshScope());
            History.ObserveSession(Session());
        }
        public void Reload() => Current = ZetlProjectSnapshotMapper.ToSnapshot(Project);
        private KastnSessionSnapshot Session() => new(KastnConnectionState.Online, "Connected", [], Current) { ServerInstanceId = "move-server" };
        public ZetlResponseEnvelope ExecuteCore(ZetlCommandEnvelope command) { var response = Service.Execute(command); Reload(); return response; }
        public Task<ZetlResponseEnvelope> Execute(ZetlCommandEnvelope command, object _) => History.ExecuteMutationAsync(command);
        public KastnDropPlan Plan(bool append = false) => KastnDropPlanner.Plan(new(Current), FirstId, true, [SecondId, FirstId],
            append ? new(KastnDropTargetKind.Bucket, NextId) : new(KastnDropTargetKind.Slip, AnchorId, KastnDropEdge.Before))!;
        public Task<KastnHistoryStepResult> Step(bool redo = false) => History.StepAsync(redo, _ => throw new InvalidOperationException("Unexpected conflict"));
        public void Dispose() => Directory.Delete(directory, recursive: true);
        private sealed class RefreshScope : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
}
