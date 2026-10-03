using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Tests.ZetlTestSupport;
using Xunit;
using System;
using System.Collections.Generic;

namespace ZETL.Tests;

public class ZetlReplayStateTests
{
    [Fact(DisplayName = "Zetl state Replay resumes queued slips across restart")]
    public static void StateReplayResumesQueuedSlipsAcrossRestart()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path, "replay-session");
        store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.ActiveBucket!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "one", "copy");
        store.AddSlip(queue, "two", "copy");

        AssertTrue(store.TryPeekNextReplaySlip(queue, out var first), "Replay bucket should expose its first slip.");
        AssertEqual("one", first?.Text, "Replay should start with the oldest current-session slip.");
        AssertTrue(store.TryConsumeReplaySlip(queue, first!.Id), "Replay should consume the first slip.");

        var reloaded = new ZetlStateStore(temp.Path, "new-session");
        var loadedQueue = reloaded.State.Projects.Single().Buckets.Single(bucket => bucket.Name == "Queue");
        AssertTrue(reloaded.TryPeekNextReplaySlip(loadedQueue, out var second), "Replay should resume a visible queued slip after restart.");
        AssertEqual("two", second?.Text, "Replay should resume at the first unconsumed slip.");
        AssertTrue(reloaded.TryConsumeReplaySlip(loadedQueue, second!.Id), "Replay should consume a prior-session slip.");
        AssertFalse(reloaded.TryPeekNextReplaySlip(loadedQueue, out _), "Replay should be empty after its last slip is consumed.");
        AssertEqual(0, loadedQueue.Slips.Count, "Consumed Replay slips should stay consumed.");
    }

    [Fact(DisplayName = "Zetl state Replay archives consumed slips for review")]
    public static void StateReplayArchivesConsumedSlipsForReview()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path, "replay-session");
        var project = store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.ActiveBucket!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "posted", "copy");

        AssertTrue(store.TryPeekNextReplaySlip(queue, out var note), "Replay bucket should expose a slip.");
        AssertTrue(store.TryConsumeReplaySlipToReview(project, queue, note!.Id, out var reviewBucket), "Replay should consume into review.");

        AssertTrue(reviewBucket is not null, "Replay consume should create a review bucket.");
        AssertEqual(queue.Id, project.ActiveBucketId, "Review archive should not steal the active bucket.");
        AssertEqual("Queue Review", reviewBucket!.Name, "Review bucket should be named from the Replay bucket.");
        AssertEqual("Standard", reviewBucket.Settings.Kind, "Review bucket should stay standard.");
        AssertEqual("posted", reviewBucket.Slips.Single().Text, "Review bucket should keep consumed text.");
        AssertEqual("replay", reviewBucket.Slips.Single().Source, "Review note should be tagged as replay.");
        AssertFalse(store.TryPeekNextReplaySlip(queue, out _), "Consumed Replay slip should leave the queue.");

        store.AddSlip(queue, "posted again", "copy");
        AssertTrue(store.TryPeekNextReplaySlip(queue, out var second), "Replay bucket should expose another slip.");
        AssertTrue(store.TryConsumeReplaySlipToReview(project, queue, second!.Id, out var sameReviewBucket), "Replay should consume into the same review bucket.");
        AssertTrue(sameReviewBucket is not null, "Replay consume should return the reused review bucket.");
        AssertEqual(reviewBucket.Id, sameReviewBucket!.Id, "Review bucket should be reused.");
        AssertEqual(2, sameReviewBucket.Slips.Count, "Review bucket should accumulate consumed Replay slips.");
    }

    [Fact(DisplayName = "Zetl state Replay restores consumed slips from review")]
    public static void StateReplayRestoresConsumedSlipsFromReview()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path, "replay-session");
        var project = store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.ActiveBucket!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "posted", "copy");

        AssertTrue(store.TryPeekNextReplaySlip(queue, out var note), "Replay bucket should expose a slip.");
        AssertTrue(
            store.TryConsumeReplaySlipToReview(project, queue, note!.Id, out var reviewBucket, out var consumedNote, out var reviewNote),
            "Replay should consume with undo details.");
        AssertTrue(consumedNote is not null, "Replay consume should return the consumed slip.");
        AssertTrue(reviewBucket is not null, "Replay consume should return the review bucket.");
        AssertTrue(reviewNote is not null, "Replay consume should return the review slip.");

        store.SetBucketKind(queue, "Standard");
        store.RestoreReplayConsumedSlip(queue, consumedNote!, reviewBucket, reviewNote?.Id);

        AssertTrue(ZetlStateRules.IsReplayBucket(queue), "Replay undo should restore Replay kind.");
        AssertEqual("posted", queue.Slips.Single().Text, "Replay undo should restore the consumed slip.");
        AssertEqual(0, reviewBucket!.Slips.Count, "Replay undo should remove the review copy.");
    }

    [Fact(DisplayName = "Zetl state maps legacy Fifo kind to Replay")]
    public static void StateMapsLegacyFifoKindToReplay()
    {
        using var temp = new TempStateFile();
        // A state file written by an older build that used the "Fifo" kind.
        var legacyJson =
            """
            { "version": 1, "activeProjectId": "p1", "projects": [ { "id": "p1", "name": "Demo", "activeBucketId": "b1", "buckets": [ { "id": "b1", "name": "Queue", "kind": "Fifo", "defaultKind": "Fifo", "fifoReviewBucketId": "b2", "notes": [] }, { "id": "b2", "name": "Queue Review", "kind": "Standard", "defaultKind": "Standard", "notes": [] } ] } ] }
            """;
        System.IO.File.WriteAllText(temp.Path, legacyJson);

        var store = new ZetlStateStore(temp.Path);
        var queue = store.ActiveBucket!;
        AssertEqual("Queue", queue.Name, "Legacy bucket should load.");
        AssertEqual("Replay", queue.Settings.Kind, "Legacy Fifo kind should load as Replay.");
        AssertEqual("Replay", queue.Settings.DefaultKind, "Legacy Fifo default kind should load as Replay.");
        AssertTrue(ZetlStateRules.IsReplayBucket(queue), "Legacy Fifo bucket should still be a Replay bucket.");
        AssertEqual("b2", queue.Settings.ReplayReviewBucketId, "Legacy Replay review links should load.");

        store.SetBucketKind(queue, queue.Settings.Kind);
        var projectPath = Directory.GetFiles(
            System.IO.Path.GetDirectoryName(temp.Path)!,
            "project.json",
            SearchOption.AllDirectories).Single();
        var savedJson = System.IO.File.ReadAllText(projectPath);
        AssertTrue(
            savedJson.Contains("\"fifoReviewBucketId\": \"b2\"", StringComparison.Ordinal),
            "Replay review links should retain their historical JSON field name.");
    }
}
