using System.Text.Json;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public class ZetlProjectChangesTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "zetl-deltas-" + Guid.NewGuid().ToString("N"));
        public ZetlStateStore Store { get; }
        public ZetlProject Project { get; }
        public ZetlProjectChangesFeed Feed { get; }
        public ZetlProjectService Service { get; }
        public ZetlBucket A => Project.Buckets.Single(b => b.Name == "A");
        public ZetlBucket B => Project.Buckets.Single(b => b.Name == "B");
        public Fixture(int versions = 128, int perProject = 32, int units = 200_000)
        {
            Directory.CreateDirectory(Root);
            Store = new(Path.Combine(Root, "state.json")); Project = Store.CreateProject("Delta test", ["A", "B"]);
            Feed = new(versions, perProject, units); Service = new(Store, changesFeed: Feed);
        }
        public ZetlProjectChanges Changes(ZetlProjectCursor? cursor = null, string? id = null, string? commandId = null)
        {
            var response = Service.Execute(ZetlCommandEnvelope.Create(commandId ?? Guid.NewGuid().ToString("N"),
                ZetlCommandKind.GetProjectChanges, new GetProjectChangesCommand { Cursor = cursor }, id ?? Project.Id));
            Assert.Equal(ZetlResponseStatus.Success, response.Status);
            return response.Payload!.Value.Deserialize<ZetlProjectChanges>(ZetlProtocolJson.Options)!;
        }
        public ZetlProjectSnapshot Snapshot() => ZetlProjectSnapshotMapper.ToSnapshot(Project);
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static void Same(ZetlProjectSnapshot expected, ZetlProjectSnapshot actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected, ZetlProtocolJson.Options), JsonSerializer.Serialize(actual, ZetlProtocolJson.Options));

    [Fact] public void BootstrapAndIdleReadsHaveAtomicCursorAndNoRemapping()
    {
        using var f = new Fixture(); f.Store.AddSlips(f.A, Enumerable.Range(0, 30).Select(i => "note " + i), "fixture");
        var first = f.Changes(); Assert.False(first.ResyncRequired); Assert.Equal("bootstrap", first.ResetReason);
        var applied = ZetlProjectChangesReducer.Apply(null, null, first); Same(f.Snapshot(), applied);
        var scans = f.Feed.IndexScans; var mappings = f.Feed.MappedSlips;
        var idle = f.Changes(first.Cursor); Assert.Null(idle.Snapshot); Assert.Null(idle.Metadata); Assert.Empty(idle.Slips);
        Assert.Equal(first.Cursor, idle.Cursor); Assert.Equal(scans, f.Feed.IndexScans); Assert.Equal(mappings, f.Feed.MappedSlips);
        Assert.Same(applied, ZetlProjectChangesReducer.Apply(applied, first.Cursor, idle));
    }
    [Fact] public void MixedDirectAndCommandEditsMovesOrderingDeletionAndRestoreReconstructSnapshot()
    {
        using var f = new Fixture(); var one = f.Store.AddSlip(f.A, "one", "fixture"); var two = f.Store.AddSlip(f.A, "two", "fixture");
        var first = f.Changes(); var applied = first.Snapshot!; var cursor = first.Cursor;
        void Check(Action action)
        {
            action(); var batch = f.Changes(cursor); Assert.Null(batch.Snapshot);
            applied = ZetlProjectChangesReducer.Apply(applied, cursor, batch); cursor = batch.Cursor; Same(f.Snapshot(), applied);
        }
        Check(() => f.Store.UpdateSlip(one, "one edited", bold: true, fontSize: 21));
        Check(() => f.Store.MoveSlip(f.Project, one, f.B));
        Check(() => f.Store.AddSlip(f.B, "three", "fixture"));
        Check(() => f.Store.ReorderSlip(f.Project, one, null));
        Check(() => f.Store.ReorderBucket(f.Project, f.B, f.A.Id));
        Check(() => f.Store.SetBucketHeading(f.A, "center", true, 2));
        Check(() =>
        {
            var deleted = f.Service.Execute(ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), ZetlCommandKind.DeleteSlip,
                new DeleteSlipCommand(), f.Project.Id, two.Id, two.Revision)); Assert.Equal(ZetlResponseStatus.Success, deleted.Status);
        });
        Assert.NotNull(applied.Slips.Single(s => s.Id == two.Id).DeletedAtUtc);
        Check(() => f.Store.MoveSlip(f.Project, two, f.B));
        Check(() => f.Store.DeleteSlip(f.B, two.Id));
        Assert.DoesNotContain(applied.Slips, s => s.Id == two.Id);
        Check(() => { var child = f.Store.AddBucket(f.Project, "child", f.A.Id); f.Store.AddSlip(child, "child note", "fixture"); });
        Check(() => f.Store.DeleteBucket(f.Project, f.A.Id));
    }
    [Fact] public void MetadataActiveBucketViewsAndBucketRenamesAreIncluded()
    {
        using var f = new Fixture(); var first = f.Changes();
        f.Store.SetActiveBucket(f.Project, f.B.Id);
        f.Store.UpdateProjectName(f.Project, "Changed name");
        f.Store.SaveProjectView(f.Project, new ZetlViewDocument { Id = "delta-view", Name = "New view" });
        f.Store.SetProjectDefaultView(f.Project, "delta-view");
        f.Store.UpdateBucketName(f.A, "Renamed A");
        var delta = f.Changes(first.Cursor); Assert.NotNull(delta.Metadata); Assert.Empty(delta.Metadata!.Slips);
        Assert.Single(delta.Buckets); Same(f.Snapshot(), ZetlProjectChangesReducer.Apply(first.Snapshot, first.Cursor, delta));
    }
    [Fact] public void CoalescesIntermediateStatesButNeverLosesCurrentContent()
    {
        using var f = new Fixture(); var slip = f.Store.AddSlip(f.A, "initial", "fixture"); var first = f.Changes();
        f.Store.UpdateSlip(slip, "middle"); f.Store.UpdateSlip(slip, "final");
        var transient = f.Store.AddSlip(f.B, "transient", "fixture"); f.Store.DeleteSlip(f.B, transient.Id);
        var delta = f.Changes(first.Cursor); Assert.Single(delta.Slips); Assert.Equal("final", delta.Slips[0].Text);
        Same(f.Snapshot(), ZetlProjectChangesReducer.Apply(first.Snapshot, first.Cursor, delta));
    }
    [Fact] public void DeletingReviewBucketIncludesNormalizedReferenceWithoutRevisionChange()
    {
        using var f = new Fixture();
        f.Store.UpdateBucket(f.Project, f.A, new("A", null, "Replay", "Replay", "Formatted", "", 5, f.B.Id, ""));
        var first = f.Changes(); var revision = f.A.Revision;
        f.Store.DeleteBucket(f.Project, f.B.Id);
        Assert.Equal(revision, f.A.Revision); Assert.Null(f.A.Settings.ReplayReviewBucketId);
        var delta = f.Changes(first.Cursor); Assert.Contains(delta.Buckets, b => b.Id == f.A.Id);
        Same(f.Snapshot(), ZetlProjectChangesReducer.Apply(first.Snapshot, first.Cursor, delta));
    }
    [Fact] public void IndependentReadersCanAdvanceAtDifferentSpeeds()
    {
        using var f = new Fixture(); var first = f.Changes(); f.Store.AddSlip(f.A, "one", "fixture");
        var fast = f.Changes(first.Cursor); f.Store.AddSlip(f.B, "two", "fixture");
        var slow = f.Changes(first.Cursor); var nextFast = f.Changes(fast.Cursor);
        Same(f.Snapshot(), ZetlProjectChangesReducer.Apply(first.Snapshot, first.Cursor, slow));
        Same(f.Snapshot(), ZetlProjectChangesReducer.Apply(ZetlProjectChangesReducer.Apply(first.Snapshot, first.Cursor, fast), fast.Cursor, nextFast));
    }
    [Fact] public void ExpirationInvalidEpochAndCrossProjectCursorsResetExplicitly()
    {
        using var f = new Fixture(versions: 2, perProject: 2); var first = f.Changes();
        f.Store.AddSlip(f.A, "one", "fixture"); var second = f.Changes(first.Cursor);
        f.Store.AddSlip(f.A, "two", "fixture"); _ = f.Changes(second.Cursor);
        var expired = f.Changes(first.Cursor); Assert.True(expired.ResyncRequired); Assert.Equal("cursor_expired", expired.ResetReason);
        Same(f.Snapshot(), expired.Snapshot!); Assert.True(f.Feed.RetainedVersions <= 2);
        Assert.Equal("epoch_changed", f.Changes(expired.Cursor with { Epoch = "old-server" }).ResetReason);
        Assert.Equal("cursor_invalid", f.Changes(expired.Cursor with { Sequence = expired.Cursor.Sequence + 1 }).ResetReason);
        var other = f.Store.CreateProject("Other", ["Inbox"]);
        Assert.Equal("cursor_invalid", f.Changes(expired.Cursor, other.Id).ResetReason);
    }
    [Fact] public void RestartAndReplacedProjectWithSameSequenceCannotReuseCursor()
    {
        using var f = new Fixture(); f.Store.AddSlip(f.A, "saved", "fixture"); var first = f.Changes();
        var reloaded = new ZetlStateStore(Path.Combine(f.Root, "state.json"));
        var service = new ZetlProjectService(reloaded, changesFeed: f.Feed);
        var response = service.Execute(ZetlCommandEnvelope.Create("reload", ZetlCommandKind.GetProjectChanges,
            new GetProjectChangesCommand { Cursor = first.Cursor }, f.Project.Id));
        var delta = response.Payload!.Value.Deserialize<ZetlProjectChanges>(ZetlProtocolJson.Options)!;
        Assert.Equal("project_replaced", delta.ResetReason); Assert.NotNull(delta.Snapshot);
        var restart = new ZetlProjectService(reloaded).Execute(ZetlCommandEnvelope.Create("restart", ZetlCommandKind.GetProjectChanges,
            new GetProjectChangesCommand { Cursor = first.Cursor }, f.Project.Id));
        Assert.Equal("epoch_changed", restart.Payload!.Value.Deserialize<ZetlProjectChanges>(ZetlProtocolJson.Options)!.ResetReason);
    }
    [Fact] public void CapacityLimitPreservesIdleEfficiencyAndResetsOnChanges()
    {
        using var f = new Fixture(units: 1); var first = f.Changes(); var scans = f.Feed.IndexScans;
        Assert.Null(f.Changes(first.Cursor).Snapshot); Assert.Equal(scans, f.Feed.IndexScans);
        f.Store.AddSlip(f.A, "new", "fixture"); var limited = f.Changes(first.Cursor);
        Assert.Equal("capacity_limit", limited.ResetReason); Assert.True(limited.ResyncRequired); Assert.Equal(0, f.Feed.RetainedIndexUnits);
    }
    [Fact] public void PictureDeltasContainMetadataOnlyAndNotAssetBytes()
    {
        using var f = new Fixture(); var first = f.Changes();
        var bytes = Enumerable.Repeat((byte)37, 128 * 1024).ToArray();
        var slip = f.Store.AddImageSlip(f.Project, f.A, new(bytes, 10, 20), "fixture");
        var delta = f.Changes(first.Cursor); Assert.Single(delta.Slips); Assert.Equal(bytes.Length, delta.Slips[0].Picture!.ByteLength);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(delta, ZetlProtocolJson.Options).Length < 4000);
        var picture = f.Service.Execute(new ZetlCommandEnvelope { CommandId = "picture", Kind = ZetlCommandKind.GetSlipPicture, ProjectId = f.Project.Id, TargetId = slip.Id });
        Assert.Equal(bytes, picture.Payload!.Value.Deserialize<ZetlPictureContent>(ZetlProtocolJson.Options)!.Bytes);
    }
    [Fact] public void LargeProjectOneEditMapsOnlyOneSlipAndPayloadShrinks()
    {
        using var f = new Fixture(); f.Store.AddSlips(f.A, Enumerable.Range(0, 2000).Select(i => $"note {i}: " + new string('x', 1024)), "fixture");
        var first = f.Changes(); var mapped = f.Feed.MappedSlips;
        f.Store.UpdateSlip(f.A.Slips[1000], "one edited note"); var delta = f.Changes(first.Cursor);
        Assert.Equal(mapped + 1, f.Feed.MappedSlips); Assert.Single(delta.Slips); Assert.Empty(delta.SlipOrders);
        var fullBytes = JsonSerializer.SerializeToUtf8Bytes(first, ZetlProtocolJson.Options).Length;
        var deltaBytes = JsonSerializer.SerializeToUtf8Bytes(delta, ZetlProtocolJson.Options).Length;
        var idleBytes = JsonSerializer.SerializeToUtf8Bytes(f.Changes(delta.Cursor), ZetlProtocolJson.Options).Length;
        output.WriteLine($"2,000-slip fixture: bootstrap payload {fullBytes:N0} bytes; one-edit delta {deltaBytes:N0} bytes; idle {idleBytes:N0} bytes; mapped slips {f.Feed.MappedSlips - mapped}.");
        Assert.True(deltaBytes * 100 < fullBytes, $"Delta {deltaBytes}, snapshot {fullBytes}");
        Same(f.Snapshot(), ZetlProjectChangesReducer.Apply(first.Snapshot, first.Cursor, delta));
    }
    [Fact] public void ReadRequestsAreNotDeduplicatedToStaleBoundariesAndMissingProjectsReturnNotFound()
    {
        using var f = new Fixture(); var first = f.Changes(commandId: "same-read-id"); f.Store.AddSlip(f.A, "new", "fixture");
        var second = f.Changes(first.Cursor, commandId: "same-read-id"); Assert.True(second.Cursor.Sequence > first.Cursor.Sequence);
        f.Store.DeleteProject(f.Project.Id);
        var missing = f.Service.Execute(ZetlCommandEnvelope.Create("missing", ZetlCommandKind.GetProjectChanges, new GetProjectChangesCommand(), f.Project.Id));
        Assert.Equal(ZetlResponseStatus.NotFound, missing.Status);
        Assert.True(ZetlContractRules.IsReadOnly(ZetlCommandKind.GetProjectChanges));
    }
    [Fact] public void ReducerRejectsGapsAndMissingOrderWithoutMutatingBaseline()
    {
        using var f = new Fixture(); var first = f.Changes(); f.Store.AddSlip(f.A, "new", "fixture"); var delta = f.Changes(first.Cursor);
        Assert.Throws<InvalidDataException>(() => ZetlProjectChangesReducer.Apply(first.Snapshot, first.Cursor, delta with { FromCursor = first.Cursor with { Token = "wrong" } }));
        Assert.Throws<InvalidDataException>(() => ZetlProjectChangesReducer.Apply(first.Snapshot, first.Cursor, delta with { SlipOrders = [] }));
        Assert.Empty(first.Snapshot!.Slips);
        Same(f.Snapshot(), ZetlProjectChangesReducer.Apply(first.Snapshot, first.Cursor, delta));
    }
    [Fact] public async Task ConcurrentCapturesDoNotCrossTheSnapshotCursorBoundary()
    {
        using var f = new Fixture(); var first = f.Changes(); var applied = first.Snapshot!; var cursor = first.Cursor;
        var captures = Task.Run(() => { for (var i = 0; i < 40; i++) f.Store.AddSlip(f.A, "parallel " + i, "fixture"); });
        do
        {
            var next = f.Changes(cursor); applied = ZetlProjectChangesReducer.Apply(applied, cursor, next); cursor = next.Cursor;
            Assert.Equal(cursor.Sequence, applied.ChangeSequence);
            Assert.Equal(applied.Slips.Count, applied.Slips.Select(s => s.Id).Distinct().Count());
            await Task.Delay(1);
        } while (!captures.IsCompleted);
        await captures;
        var final = f.Changes(cursor); applied = ZetlProjectChangesReducer.Apply(applied, cursor, final);
        Same(f.Snapshot(), applied); Assert.Equal(40, applied.Slips.Count);
    }
}
