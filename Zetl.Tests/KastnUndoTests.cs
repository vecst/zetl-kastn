using System.Text.Json;
using KASTN;
using ZETL.Contracts;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class KastnUndoTests
{
    private static ZetlSlipSnapshot Slip(
        string id,
        long revision,
        string bucketId,
        string text = "body",
        string title = "",
        string? align = null,
        string blockKind = "",
        bool excluded = false,
        bool ignoreBucketRenderKind = false,
        bool isChecked = false,
        IReadOnlyList<ZetlInlineStyleRange>? inlineStyles = null) => new()
    {
        Id = id,
        Revision = revision,
        Type = ZetlSlipType.Text,
        BucketId = bucketId,
        Text = text,
        Title = title,
        Align = align,
        BlockKind = blockKind,
        ExcludedFromViews = excluded,
        IgnoreBucketRenderKind = ignoreBucketRenderKind,
        Checked = isChecked,
        InlineStyles = inlineStyles ?? [],
        Source = "kastn",
        CapturedAtUtc = DateTimeOffset.UnixEpoch
    };

    private static KastnUndoOperation Op(
        ZetlSlipSnapshot from,
        KastnSlipMemento to,
        string? fromFollowing = null,
        string? toFollowing = null) =>
        new(from.Id, from, fromFollowing, to, toFollowing);

    private static T Payload<T>(KastnInverseStep step) =>
        step.Payload.Deserialize<T>(ZetlProtocolJson.Options)
        ?? throw new InvalidOperationException("Inverse payload did not deserialize.");

    [Fact] public void HistoryPopsNewestFirstAndTruncates()
    {
        var history = new KastnUndoHistory(capacity: 2);
        AssertFalse(history.CanUndo, "A fresh history has nothing to undo.");

        history.Push(Entry("one"));
        history.Push(Entry("two"));
        history.Push(Entry("three"));

        AssertEqual(2, history.Count, "Capacity should bound the history.");
        AssertEqual("three", history.NextDescription, "The newest entry should be next.");

        AssertTrue(history.TryPop(out var newest) && newest!.Description == "three", "Pop returns the newest.");
        AssertTrue(history.TryPop(out var older) && older!.Description == "two", "The oldest survivor remains.");
        AssertFalse(history.TryPop(out _), "An emptied history pops nothing.");

        static KastnUndoEntry Entry(string description) => new(description, "p", []);
    }

    [Fact] public void DeleteMementoInvertsToASoftDelete()
    {
        var slip = Slip("s1", 5, "deleted");
        var steps = KastnUndoPlanner.BuildSteps(Op(slip, KastnSlipMemento.Delete));

        AssertEqual(1, steps.Count, "A delete inverts in one step.");
        AssertEqual(ZetlCommandKind.DeleteSlip, steps[0].Kind, "Undoing an add soft-deletes the slip.");
        AssertFalse(steps[0].BestEffort, "The delete is the meaningful step.");
    }

    [Fact] public void RestoreToAnotherBucketMovesThenReorders()
    {
        var from = Slip("s1", 9, "b2", text: "same");
        var target = Slip("s1", 4, "b1", text: "same");

        var steps = KastnUndoPlanner.BuildSteps(
            Op(from, KastnSlipMemento.To(target), fromFollowing: "x", toFollowing: "y"));

        AssertEqual(2, steps.Count, "A pure move restores bucket then position.");
        AssertEqual(ZetlCommandKind.MoveSlip, steps[0].Kind, "The bucket is restored first.");
        AssertEqual("b1", Payload<MoveSlipCommand>(steps[0]).DestinationBucketId, "Move targets the original bucket.");
        AssertEqual(ZetlCommandKind.ReorderSlip, steps[1].Kind, "Position is restored second.");
        AssertTrue(steps[1].BestEffort, "Position restore is best-effort.");
        AssertEqual("y", Payload<ReorderSlipCommand>(steps[1]).BeforeSlipId, "Reorder uses the target neighbour.");
    }

    [Fact] public void RestoreOfPropertiesOnlyEmitsOneUpdate()
    {
        var from = Slip("s1", 9, "b1", text: "changed");
        var target = Slip(
            "s1", 4, "b1", text: "original", title: "T", align: "center",
            blockKind: "bullet", excluded: true, ignoreBucketRenderKind: true, isChecked: true,
            inlineStyles: [new ZetlInlineStyleRange { Start = 0, Length = 1, Kind = "bold" }]);

        var steps = KastnUndoPlanner.BuildSteps(Op(from, KastnSlipMemento.To(target), "a", "a"));

        AssertEqual(1, steps.Count, "Same bucket and position needs only an update.");
        var restore = Payload<UpdateSlipCommand>(steps[0]);
        AssertEqual("original", restore.Text, "Text is restored.");
        AssertEqual("T", restore.Title, "Title is restored.");
        AssertEqual("center", restore.Align, "Alignment is restored.");
        AssertEqual("bullet", restore.BlockKind, "Block kind is restored.");
        AssertEqual(true, restore.ExcludedFromViews, "Visibility is restored.");
        AssertEqual(true, restore.IgnoreBucketRenderKind, "Render override is restored.");
        AssertEqual(true, restore.Checked, "Checked state is restored.");
        AssertEqual(1, restore.InlineStyles?.Count ?? 0, "Inline styles are restored.");
    }

    [Fact] public void RestoreForcesLeftAlignmentExplicitly()
    {
        var from = Slip("s1", 2, "b1", align: "right");
        var target = Slip("s1", 1, "b1", align: null);

        var steps = KastnUndoPlanner.BuildSteps(Op(from, KastnSlipMemento.To(target), "a", "a"));

        AssertEqual("left", Payload<UpdateSlipCommand>(steps[0]).Align, "A previously-unaligned slip returns to left.");
    }

    [Fact] public void ReorderOnlyEmitsASingleReorder()
    {
        var from = Slip("s1", 3, "b1", text: "same");
        var target = Slip("s1", 2, "b1", text: "same");

        var steps = KastnUndoPlanner.BuildSteps(
            Op(from, KastnSlipMemento.To(target), fromFollowing: "x", toFollowing: "z"));

        AssertEqual(1, steps.Count, "Only the position changed.");
        AssertEqual(ZetlCommandKind.ReorderSlip, steps[0].Kind, "A reorder restores position.");
    }

    [Fact] public void UnchangedOperationIsANoOp()
    {
        var slip = Slip("s1", 4, "b1", text: "same");
        var op = Op(slip, KastnSlipMemento.To(slip with { Revision = 9 }), "a", "a");

        AssertTrue(KastnUndoPlanner.IsNoOp(op), "Identical state needs no inverse.");
        AssertEqual(0, KastnUndoPlanner.BuildSteps(op).Count, "A no-op yields no steps.");
    }

    [Fact] public void OppositeSwapsFromAndTo()
    {
        var from = Slip("s1", 9, "b2");
        var target = Slip("s1", 4, "b1");
        var op = Op(from, KastnSlipMemento.To(target), fromFollowing: "fa", toFollowing: "fb");

        var now = Slip("s1", 12, "b1");
        var opposite = KastnUndoPlanner.Opposite(op, now);

        AssertEqual(now, opposite.From, "The opposite starts from the applied state.");
        AssertEqual("fb", opposite.FromFollowing, "The opposite's source neighbour is the restored neighbour.");
        AssertTrue(opposite.To.Restore is not null && opposite.To.Restore!.BucketId == "b2", "The opposite restores the original From state.");
        AssertEqual("fa", opposite.ToFollowing, "The opposite targets the original source neighbour.");
    }

    [Fact] public void OppositeOfADeleteRestoresTheSlip()
    {
        var created = Slip("s1", 3, "b1");
        var op = Op(created, KastnSlipMemento.Delete);
        var nowDeleted = created with { Revision = 4, BucketId = "deleted" };

        var opposite = KastnUndoPlanner.Opposite(op, nowDeleted);

        AssertTrue(opposite.To.Restore is not null, "Redoing an add restores the slip rather than deleting again.");
        AssertEqual("b1", opposite.To.Restore!.BucketId, "The slip returns to its created bucket.");
    }

    [Fact] public void RethreadRevisionRepointsStackedOperationsOnTheSameSlip()
    {
        // Two stacked entries on one slip: undoing the newer advances the slip's
        // revision, so the older entry must be re-pointed at it or the second undo
        // in a row conflicts and is skipped ("only one undo works").
        var history = new KastnUndoHistory();
        history.Push(new KastnUndoEntry("Edit slip", "p",
            [Op(Slip("s1", 2, "b1", text: "after bold"), KastnSlipMemento.To(Slip("s1", 1, "b1")))]));
        history.Push(new KastnUndoEntry("Edit slip", "p",
            [Op(Slip("other", 7, "b1"), KastnSlipMemento.To(Slip("other", 6, "b1")))]));

        history.RethreadRevision("s1", 5);

        AssertTrue(history.TryPop(out var untouched), "The unrelated entry pops first.");
        AssertEqual(
            7,
            untouched!.Operations[0].From.Revision,
            "Operations on other slips keep their recorded revision.");

        AssertTrue(history.TryPop(out var rethreaded), "The same-slip entry remains poppable.");
        AssertEqual(
            5,
            rethreaded!.Operations[0].From.Revision,
            "A stacked operation on the changed slip expects the new current revision.");
        AssertEqual(
            "after bold",
            rethreaded.Operations[0].From.Text,
            "Re-threading only repoints the revision; the recorded states stay intact.");
        AssertEqual(
            1,
            rethreaded.Operations[0].To.Restore!.Revision,
            "The restore target is untouched.");
    }

    private static ZetlBucketSnapshot Bucket(
        string id,
        long revision,
        string name,
        string? parentId = null,
        string renderKind = "",
        string headingAlign = "",
        bool headingBold = false,
        int headingLevel = 0) => new()
    {
        Id = id,
        Revision = revision,
        Name = name,
        ParentBucketId = parentId,
        RenderKind = renderKind,
        HeadingAlign = headingAlign,
        HeadingBold = headingBold,
        HeadingLevel = headingLevel
    };

    [Fact] public void BucketRestoreEmitsOnlyTheNeededSteps()
    {
        // Rename-only: one UpdateBucket restore.
        var renamed = new KastnBucketUndoOperation(
            "b1", Bucket("b1", 5, "Renamed"), "sib", Bucket("b1", 4, "Original"), "sib");
        var renameSteps = KastnUndoPlanner.BuildBucketSteps(renamed);
        AssertEqual(1, renameSteps.Count, "A rename restores with one update.");
        AssertEqual(ZetlCommandKind.UpdateBucket, renameSteps[0].Kind, "The update restores identity.");
        AssertEqual(
            "Original",
            renameSteps[0].Payload.Deserialize<UpdateBucketCommand>(ZetlProtocolJson.Options)!.Name,
            "The restore carries the prior name.");

        // Heading-only: one SetBucketHeading restore.
        var restyled = new KastnBucketUndoOperation(
            "b1", Bucket("b1", 5, "Same", headingBold: true), "sib", Bucket("b1", 4, "Same"), "sib");
        var headingSteps = KastnUndoPlanner.BuildBucketSteps(restyled);
        AssertEqual(1, headingSteps.Count, "A heading change restores with one step.");
        AssertEqual(ZetlCommandKind.SetBucketHeading, headingSteps[0].Kind, "Heading styling has its own command.");

        // Reorder-only: one best-effort ReorderBucket with the prior anchor.
        var reordered = new KastnBucketUndoOperation(
            "b1", Bucket("b1", 5, "Same"), FromFollowing: "x", Bucket("b1", 4, "Same"), ToFollowing: "y");
        var reorderSteps = KastnUndoPlanner.BuildBucketSteps(reordered);
        AssertEqual(1, reorderSteps.Count, "Only the position changed.");
        AssertEqual(ZetlCommandKind.ReorderBucket, reorderSteps[0].Kind, "A reorder restores position.");
        AssertTrue(reorderSteps[0].BestEffort, "Position restore is best-effort.");
        AssertEqual(
            "y",
            reorderSteps[0].Payload.Deserialize<ReorderBucketCommand>(ZetlProtocolJson.Options)!.BeforeBucketId,
            "Reorder uses the prior anchor.");

        // Reparent: identity restore plus a position restore under the old parent.
        var reparented = new KastnBucketUndoOperation(
            "b1", Bucket("b1", 5, "Same", parentId: "p2"), "x", Bucket("b1", 4, "Same", parentId: "p1"), "x");
        var reparentSteps = KastnUndoPlanner.BuildBucketSteps(reparented);
        AssertEqual(2, reparentSteps.Count, "A reparent restores parent then position.");
        AssertEqual(ZetlCommandKind.UpdateBucket, reparentSteps[0].Kind, "The parent is restored first.");
        AssertEqual(ZetlCommandKind.ReorderBucket, reparentSteps[1].Kind, "Position is restored second.");

        // Unchanged: a no-op that should not be recorded.
        var unchanged = new KastnBucketUndoOperation(
            "b1", Bucket("b1", 5, "Same"), "x", Bucket("b1", 4, "Same"), "x");
        AssertTrue(KastnUndoPlanner.IsNoOp(unchanged), "Identical state needs no inverse.");
    }

    [Fact] public void BucketOppositeSwapsFromAndTo()
    {
        var op = new KastnBucketUndoOperation(
            "b1", Bucket("b1", 5, "Renamed"), "fa", Bucket("b1", 4, "Original"), "fb");
        var now = Bucket("b1", 6, "Original");

        var opposite = KastnUndoPlanner.Opposite(op, now);

        AssertEqual(now, opposite.From, "The opposite starts from the applied state.");
        AssertEqual("fb", opposite.FromFollowing, "The opposite's source anchor is the restored anchor.");
        AssertEqual("Renamed", opposite.To.Name, "The opposite restores the original From state.");
        AssertEqual("fa", opposite.ToFollowing, "The opposite targets the original source anchor.");
    }

    [Fact] public void FollowingBucketIdSkipsOtherParents()
    {
        var project = new ZetlProjectSnapshot
        {
            Id = "p",
            Name = "Project",
            MetadataRevision = 1,
            ChangeSequence = 1,
            Buckets =
            [
                Bucket("top1", 1, "Top 1"),
                Bucket("child1", 1, "Child 1", parentId: "top1"),
                Bucket("top2", 1, "Top 2"),
                Bucket("child2", 1, "Child 2", parentId: "top1")
            ]
        };

        AssertEqual("top2", KastnUndoPlanner.FollowingBucketId(project, "top1"), "Nested children are skipped.");
        AssertEqual("child2", KastnUndoPlanner.FollowingBucketId(project, "child1"), "Siblings share a parent.");
        AssertTrue(KastnUndoPlanner.FollowingBucketId(project, "top2") is null, "The last sibling has no follower.");
        AssertTrue(KastnUndoPlanner.FollowingBucketId(project, "missing") is null, "An unknown bucket has no follower.");
    }

    [Fact] public void RethreadBucketRevisionRepointsStackedBucketOperations()
    {
        var history = new KastnUndoHistory();
        history.Push(new KastnUndoEntry("Edit bucket", "p", [])
        {
            BucketOperations =
                [new KastnBucketUndoOperation("b1", Bucket("b1", 2, "Renamed"), null, Bucket("b1", 1, "Original"), null)]
        });

        history.RethreadBucketRevision("b1", 7);

        AssertTrue(history.TryPop(out var entry), "The entry remains poppable.");
        AssertEqual(
            7,
            entry!.BucketOperations[0].From.Revision,
            "A stacked bucket operation expects the new current revision.");
        AssertEqual(
            1,
            entry.BucketOperations[0].To.Revision,
            "The restore target is untouched.");
    }

    [Fact] public void FollowingSlipIdSkipsOtherBuckets()
    {
        var project = new ZetlProjectSnapshot
        {
            Id = "p",
            Name = "Project",
            MetadataRevision = 1,
            ChangeSequence = 1,
            Slips =
            [
                Slip("s1", 1, "b1"),
                Slip("s2", 1, "b1"),
                Slip("s3", 1, "b2"),
                Slip("s4", 1, "b1")
            ]
        };

        AssertEqual("s2", KastnUndoPlanner.FollowingSlipId(project, "b1", "s1"), "The next same-bucket slip follows.");
        AssertEqual("s4", KastnUndoPlanner.FollowingSlipId(project, "b1", "s2"), "Slips in other buckets are skipped.");
        AssertTrue(KastnUndoPlanner.FollowingSlipId(project, "b1", "s4") is null, "The last slip has no follower.");
        AssertTrue(KastnUndoPlanner.FollowingSlipId(project, "b2", "s3") is null, "A lone bucket slip has no follower.");
    }
}
