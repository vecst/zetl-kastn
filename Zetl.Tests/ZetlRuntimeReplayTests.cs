using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Tests.ZetlTestSupport;
using Xunit;
using System;
using System.Collections.Generic;

namespace ZETL.Tests;

public class ZetlRuntimeReplayTests
{
    [Fact(DisplayName = "Runtime Replay pastes a dual slip as text")]
    public static void RuntimeReplayPastesDualSlipAsText()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        store.AddImageSlip(
            project,
            queue,
            new ZetlClipboardImage([5, 5, 5], 2, 2),
            "copy",
            caption: "A1\tB1",
            preferTextContent: true);
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out var keyboard,
            out _,
            replayResumeClipboard: false);

        var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertTrue(handled, "Dual Replay should suppress the physical paste.");
        AssertEqual(1, keyboard.PasteCount, "Dual Replay should send one synthetic paste.");
        AssertEqual("A1\tB1", clipboard.Text, "Dual Replay should paste the preferred text representation.");
        AssertEqual(0, queue.Slips.Count, "Dual Replay should consume the queued slip.");
        var review = project.Buckets.Single(item => item.Id == queue.Settings.ReplayReviewBucketId);
        var reviewNote = review.Slips.Single();
        AssertFalse(reviewNote.IsImage, "The review copy should stay text-preferred.");
        AssertTrue(reviewNote.Image is not null, "The review copy should retain the attached picture.");
    }

    [Fact(DisplayName = "Runtime Replay tap consumes and restores clipboard")]
    public static void RuntimeReplayTapConsumesAndRestoresClipboard()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "queued value", "copy");
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out var keyboard,
            out var undo);

        var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertTrue(handled, "Replay tap should suppress the physical paste.");
        AssertEqual(1, keyboard.PasteCount, "Replay tap should send one synthetic paste.");
        AssertEqual("user clipboard", clipboard.Text, "Replay should restore the user's clipboard.");
        AssertEqual(0, queue.Slips.Count, "Replay should consume the queued note.");
        var review = project.Buckets.Single(bucket => bucket.Id == queue.Settings.ReplayReviewBucketId);
        AssertEqual("queued value", review.Slips.Single().Text, "Replay should archive the consumed note.");
        AssertEqual("Standard", queue.Settings.Kind, "An empty Replay bucket should return to Standard.");
        AssertTrue(undo.TryPop(false, out _), "Replay consumption should be undoable.");
    }

    [Fact(DisplayName = "Runtime Replay and pass-through let a paste into a file view through")]
    public static void RuntimeReplayAndPassThroughIgnoreFileViewPastes()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "queued value", "copy");
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var fileViewFocused = true;
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out var keyboard,
            out _,
            isFileViewFocused: () => fileViewFocused,
            passThrough: true);

        AssertFalse(
            coordinator.OnTapDispatched(ShortcutContext(VK_V)),
            "Pasting into File Explorer should paste the user's files, not a Replay item.");
        AssertEqual(0, keyboard.PasteCount, "Replay sends nothing into a file view.");
        AssertEqual("queued value", queue.Slips.Single().Text, "The Replay item waits for a text target.");

        store.SetBucketKind(queue, "Standard");
        store.AddSlip(queue, "copied value", ZetlStateRules.AutoCopySource);
        clipboard.SetState("copied value", changeToken: 2);
        AssertFalse(coordinator.OnTapDispatched(ShortcutContext(VK_V)), "The file paste goes through.");
        AssertEqual(2, queue.Slips.Count, "A paste of files doesn't pass a copy through.");

        store.DeleteSlip(queue, queue.Slips.Last().Id);
        store.SetBucketKind(queue, "Replay");
        fileViewFocused = false;
        AssertTrue(coordinator.OnTapDispatched(ShortcutContext(VK_V)), "Back in a text target, Replay takes the paste.");
        AssertEqual(1, keyboard.PasteCount, "Replay pastes its item into the text target.");
    }

    [Fact(DisplayName = "Runtime Replay clipboard session reports restore outcomes")]
    public static void RuntimeReplayClipboardSessionReportsRestoreOutcomes()
    {
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var session = new ZetlReplayClipboardSession(clipboard);
        var item = ZetlClipboardSnapshot.FromText("replay item");

        AssertEqual(
            ZetlClipboardRestoreOutcome.NoBackup,
            session.RestoreOriginalIfOwned(shifted: false),
            "A fresh Replay lane should report that it has no user backup.");
        AssertTrue(
            session.PreserveUserClipboard(shifted: false, out var failureReason) == ZetlReplayBackupOutcome.Preserved,
            $"Replay should preserve the initial clipboard: {failureReason}");
        AssertTrue(
            session.TryStage(shifted: false, item, out var injectedToken),
            "Replay should stage its item through the content policy.");
        AssertEqual(
            ZetlClipboardRestoreOutcome.Restored,
            session.RestoreIfOwned(shifted: false, item, injectedToken),
            "An unchanged staged clipboard should restore successfully.");
        AssertEqual(
            "user clipboard",
            clipboard.Text,
            "A successful restore should put back the preserved content.");

        AssertTrue(
            session.TryStage(shifted: false, item, out injectedToken),
            "Replay should stage another item after a successful restore.");
        AssertTrue(
            clipboard.SetRichText("new user copy", "<strong>new user copy</strong>"),
            "The test should replace the staged clipboard with newer content.");
        AssertEqual(
            ZetlClipboardRestoreOutcome.OwnershipLost,
            session.RestoreIfOwned(shifted: false, item, injectedToken),
            "A newer clipboard generation should cancel restoration.");
        AssertEqual(
            "<strong>new user copy</strong>",
            clipboard.RichHtml,
            "Ownership loss must leave the newer rich clipboard intact.");

        session.Reset(shifted: false);
        AssertTrue(
            session.PreserveUserClipboard(shifted: false, out failureReason) == ZetlReplayBackupOutcome.Preserved,
            $"Replay should preserve the newer user clipboard: {failureReason}");
        AssertTrue(
            session.TryStage(shifted: false, item, out injectedToken),
            "Replay should stage before the injected restore failure.");
        clipboard.SetTextSucceeds = false;
        AssertEqual(
            ZetlClipboardRestoreOutcome.Failed,
            session.RestoreIfOwned(shifted: false, item, injectedToken),
            "A backend restore rejection should have an explicit failed outcome.");

        clipboard.SetTextSucceeds = true;
        clipboard.WriteResultOverride = null;
        session.Reset(shifted: false);
        AssertTrue(
            session.PreserveUserClipboard(shifted: false, out failureReason) == ZetlReplayBackupOutcome.Preserved,
            $"Replay should preserve before testing an uncertain rollback: {failureReason}");
        AssertTrue(
            session.TryStage(shifted: false, item, out injectedToken),
            "Replay should stage before an uncertain restore failure.");
        clipboard.WriteResultOverride = new(
            ZetlClipboardWriteStatus.WriteFailedRestoreFailed,
            FailureReason: "injected partial rollback");
        AssertEqual(
            ZetlClipboardRestoreOutcome.FailedClipboardUncertain,
            session.RestoreIfOwned(shifted: false, item, injectedToken),
            "Replay must distinguish a failed restore whose rollback was also partial.");
    }

    [Fact(DisplayName = "Runtime clipboard content writer chooses the richest representation")]
    public static void RuntimeClipboardContentWriterChoosesRichestRepresentation()
    {
        var clipboard = new FakeClipboard("before", changeToken: 1);
        var nativeFormats = new[]
        {
            new ZetlClipboardFormatData(
                13,
                System.Text.Encoding.Unicode.GetBytes("native text\0"))
        };

        AssertTrue(
            ZetlClipboardContentWriter.TryWrite(
                clipboard,
                "plain fallback",
                "<strong>HTML fallback</strong>",
                nativeFormats),
            "Native Replay formats should be writable through the content policy.");
        AssertTrue(
            ReferenceEquals(nativeFormats, clipboard.LastRestoredRawFormats),
            "Native formats should take priority over HTML and plain text.");

        AssertTrue(
            ZetlClipboardContentWriter.TryWrite(
                clipboard,
                "rich fallback",
                "<em>rich fallback</em>",
                replayFormats: null),
            "HTML should be used when no native representation exists.");
        AssertEqual(
            "<em>rich fallback</em>",
            clipboard.RichHtml,
            "The rich representation should reach the backend.");

        AssertTrue(
            ZetlClipboardContentWriter.TryWrite(
                clipboard,
                "plain only",
                html: null,
                replayFormats: null),
            "Plain text should remain the final fallback.");
        AssertEqual("plain only", clipboard.Text, "The plain fallback should reach the backend.");
    }

    [Fact(DisplayName = "Runtime Replay resumes visible items after restart")]
    public static void RuntimeReplayResumesVisibleItemsAfterRestart()
    {
        using var temp = new TempStateFile();
        var firstSession = new ZetlStateStore(temp.Path, "first-session");
        firstSession.CreateProject("Demo", ["Queue"], "Queue");
        var originalQueue = firstSession.GetActiveBucket()!;
        firstSession.SetBucketKind(originalQueue, "Replay");
        firstSession.AddSlip(originalQueue, "first queued value", "copy");
        firstSession.AddSlip(originalQueue, "second queued value", "copy");

        var store = new ZetlStateStore(temp.Path, "restarted-session");
        var queue = store.GetActiveBucket()!;
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out var keyboard,
            out _);

        var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertTrue(handled, "Replay should handle a visible prior-session queue item.");
        AssertEqual(1, keyboard.PasteCount, "Replay should paste rather than pass through an apparently empty queue.");
        AssertEqual("first queued value", clipboard.Text, "Mid-queue the pasted item stays on the clipboard; the user's comes back when Replay ends.");
        AssertEqual(1, queue.Slips.Count, "Replay should consume exactly the first visible queued item.");
        AssertEqual("second queued value", queue.Slips.Single().Text, "Replay should leave the next item queued.");
        AssertEqual("Replay", queue.Settings.Kind, "Replay should remain enabled while a visible item remains.");
    }

    [Fact(DisplayName = "Runtime Replay archives an item once the app reads it")]
    public static void RuntimeReplayArchivesReadItem()
    {
        using var temp = new TempStateFile();
        var (_, queue, clipboard, _, _, coordinator) = StagedReplay(temp, appReads: true);

        coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertEqual("one", clipboard.Text, "The item was staged for the paste.");
        AssertEqual("two", queue.Slips.First().Text, "Read by the app, so it moved on.");
    }

    [Fact(DisplayName = "Runtime Replay keeps an item no app read")]
    public static void RuntimeReplayKeepsUnreadItem()
    {
        using var temp = new TempStateFile();
        var (_, queue, clipboard, notifications, keyboard, coordinator) = StagedReplay(temp, appReads: false);

        coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertEqual(3, queue.Slips.Count, "Nothing read it, so nothing was archived.");
        AssertEqual("one", queue.Slips.First().Text, "The item stays at the front.");
        AssertTrue(
            notifications.Messages.Any(message => message.Contains("didn't land", StringComparison.Ordinal)),
            "The user hears the paste didn't land.");

        coordinator.OnTapDispatched(ShortcutContext(VK_V));
        AssertEqual(2, keyboard.PasteCount, "The next Ctrl+V pastes again.");
        AssertEqual("one", clipboard.Text, "It tries the same item, not the next one.");
        AssertEqual(3, queue.Slips.Count, "Still unread, still kept.");

        // Ends the late-read watch, as the next copy would.
        clipboard.LastStaged!.MarkReplaced();
    }

    [Fact(DisplayName = "Runtime Replay settles a late read before the next paste")]
    public static async Task RuntimeReplaySettlesLateRead()
    {
        using var temp = new TempStateFile();
        var (store, queue, clipboard, notifications, _, coordinator) = StagedReplay(temp, appReads: false);

        coordinator.OnTapDispatched(ShortcutContext(VK_V));
        AssertEqual("one", queue.Slips.First().Text, "Unread in time, so kept for now.");

        // The app gets to it after all.
        clipboard.ReadStagedPastes = true;
        clipboard.LastStaged!.MarkRead();
        for (var wait = 0; wait < 100 && queue.Slips.Count == 3; wait++)
        {
            await Task.Delay(10);
        }

        AssertEqual("two", queue.Slips.First().Text, "The late read archived the item it pasted.");
        coordinator.OnTapDispatched(ShortcutContext(VK_V));
        // The late-read watch may still hold the lane for a moment. Wait for
        // the paste to finish completely (its message comes after the
        // store has written the project), not just for the queue to move.
        AssertTrue(
            notifications.WaitForCount(2, TimeSpan.FromSeconds(10)),
            "The next paste finishes: 'didn't land', then 'pasted next item'.");

        AssertEqual("two", clipboard.Text, "The next paste moves on rather than pasting 'one' twice.");
        AssertEqual("three", queue.Slips.Single().Text, "One left.");
        var review = store.State.Projects.SelectMany(project => project.Buckets)
            .Single(bucket => bucket.Id == queue.Settings.ReplayReviewBucketId);
        AssertEqual("one|two", string.Join("|", review.Slips.Select(slip => slip.Text)), "Each item is archived once, in order.");
    }

    [Fact(DisplayName = "Runtime Replay brings the user's clipboard back when it ends, not between pastes")]
    public static async Task RuntimeReplayRestoresClipboardOnlyWhenItEnds()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        foreach (var value in new[] { "one", "two", "three" })
        {
            store.AddSlip(queue, value, "copy");
        }

        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out var keyboard,
            out _);

        coordinator.OnTapDispatched(ShortcutContext(VK_V));
        coordinator.OnTapDispatched(ShortcutContext(VK_V));
        AssertEqual("two", clipboard.Text, "Between pastes the clipboard is never put back, so a slow app still reads the item.");

        // Turning Replay off brings the user's clipboard back.
        await coordinator.HandleHoldAsync(ShortcutContext(VK_R));
        AssertEqual("user clipboard", clipboard.Text, "Turning Replay off restores the user's clipboard.");

        // Replay ended some other way (the bucket changed on the Board): the
        // next ordinary paste restores first, then goes through.
        await coordinator.HandleHoldAsync(ShortcutContext(VK_R));
        store.AddSlip(queue, "four", "copy");
        coordinator.OnTapDispatched(ShortcutContext(VK_V));
        AssertEqual("three", clipboard.Text, "Replay pasted the next item, mid-queue.");
        store.SetBucketKind(queue, "Standard");
        var pastesBefore = keyboard.PasteCount;
        AssertTrue(coordinator.OnTapDispatched(ShortcutContext(VK_V)), "The paste waits for the restore.");
        AssertEqual("user clipboard", clipboard.Text, "The user's clipboard is back before their paste.");
        AssertEqual(pastesBefore + 1, keyboard.PasteCount, "Then their own paste goes through.");
    }

    [Fact(DisplayName = "Runtime rapid Replay taps consume distinct slips")]
    public static void RuntimeRapidReplayTapsConsumeDistinctSlips()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "first queued value", "copy");
        store.AddSlip(queue, "second queued value", "copy");
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            notifications,
            out var keyboard,
            out _,
            replayResumeClipboard: false);
        var firstPaste = keyboard.DeferNextPaste();

        var firstHandled = coordinator.OnTapDispatched(ShortcutContext(VK_V));
        var secondHandled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertTrue(firstHandled && secondHandled, "Both rapid Replay taps should be handled.");
        AssertEqual(1, keyboard.PasteCount, "The second same-lane tap must wait for the first paste outcome.");
        AssertEqual(2, queue.Slips.Count, "No slip should be consumed before the first paste succeeds.");

        firstPaste.SetResult(true);
        AssertTrue(
            notifications.WaitForCount(2, TimeSpan.FromSeconds(5)),
            "Both serialized Replay taps should persist and report completion within the timeout.");
        AssertEqual(2, keyboard.PasteCount, "Both serialized Replay taps should send a paste.");
        AssertEqual(0, queue.Slips.Count, "Both serialized Replay taps should consume their slips.");
        var review = project.Buckets.Single(bucket => bucket.Id == queue.Settings.ReplayReviewBucketId);
        AssertEqual(2, review.Slips.Count, "Rapid taps should archive two distinct slips, not paste one twice.");
        AssertEqual(2, review.Slips.Select(note => note.Id).Distinct().Count(), "Each consumed Replay slip should remain distinct.");
    }

    [Fact(DisplayName = "Runtime Replay lanes progress independently")]
    public static void RuntimeReplayLanesProgressIndependently()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Main", ["Queue"], "Queue");
        var mainQueue = store.GetActiveBucket()!;
        store.SetBucketKind(mainQueue, "Replay");
        store.AddSlip(mainQueue, "main queued value", "copy");
        store.CreateProject("Alternate", ["Queue"], "Queue", shifted: true);
        var shiftQueue = store.GetActiveBucket(true)!;
        store.SetBucketKind(shiftQueue, "Replay");
        store.AddSlip(shiftQueue, "alternate queued value", "copy");
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            notifications,
            out var keyboard,
            out _,
            replayResumeClipboard: false);
        var mainPaste = keyboard.DeferNextPaste();
        var shiftPaste = keyboard.DeferNextPaste();

        var mainHandled = coordinator.OnTapDispatched(ShortcutContext(VK_V));
        var shiftHandled = coordinator.OnTapDispatched(ShortcutContext(VK_V, shifted: true));

        AssertTrue(mainHandled && shiftHandled, "Replay taps in both lanes should be handled.");
        AssertEqual(1, keyboard.PasteCount, "The shared clipboard must remain staged for Main until its paste lands.");

        mainPaste.SetResult(true);
        AssertTrue(
            notifications.WaitForCount(1, TimeSpan.FromSeconds(5)),
            "Main Replay should persist and report its result within the timeout.");
        AssertTrue(
            SpinWait.SpinUntil(() => keyboard.PasteCount == 2, TimeSpan.FromSeconds(5)),
            "Alternate Replay should begin after Main releases the shared clipboard.");
        shiftPaste.SetResult(true);
        AssertTrue(
            notifications.WaitForCount(2, TimeSpan.FromSeconds(5)),
            "Alternate Replay should persist and report its result within the timeout.");
        AssertEqual(0, mainQueue.Slips.Count, "Main Replay should consume its slip.");
        AssertEqual(0, shiftQueue.Slips.Count, "Alternate Replay should consume its slip.");
    }

    [Fact(DisplayName = "Runtime Replay suppresses taps during final clipboard restoration")]
    public static void RuntimeReplaySuppressesTapDuringFinalRestore()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "final queued value", "copy");
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var dispatcher = new QueuingDispatcher();
        var restoreDelay = new ManualDelay();
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            notifications,
            out var keyboard,
            out _,
            delay: restoreDelay,
            dispatcher: dispatcher);

        AssertTrue(
            coordinator.OnTapDispatched(ShortcutContext(VK_V)),
            "The final Replay tap should be handled.");
        AssertTrue(
            dispatcher.RunUntil(
                () => keyboard.PasteCount == 1 && queue.Slips.Count == 0,
                TimeSpan.FromSeconds(5)),
            "Replay should consume its final item before the restore delay settles.");

        AssertEqual("Replay", queue.Settings.Kind, "The bucket must remain Replay while its clipboard is restoring.");
        AssertFalse(
            notifications.Messages.Exists(message => message.Contains("complete", StringComparison.OrdinalIgnoreCase)),
            "Replay must not announce completion before clipboard restoration settles.");
        AssertTrue(
            coordinator.OnTapDispatched(ShortcutContext(VK_V)),
            "A tap during final restoration should remain suppressed.");
        dispatcher.RunAll();
        AssertEqual(1, keyboard.PasteCount, "A suppressed tap must not paste the staged final item again.");

        restoreDelay.Release();
        AssertTrue(
            dispatcher.RunUntil(
                () => queue.Settings.Kind == "Standard"
                    && notifications.Messages.Exists(message =>
                        message.Contains("replay complete", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(5)),
            "Replay should finalize only after the delayed clipboard restoration settles.");
        AssertEqual("user clipboard", clipboard.Text, "Finalization should restore the original clipboard.");
        AssertEqual(1, keyboard.PasteCount, "Finalization must not inject another paste.");
    }

    [Fact(DisplayName = "Runtime Replay final restoration remains lane-local")]
    public static void RuntimeReplayFinalRestoreRemainsLaneLocal()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Main", ["Queue"], "Queue");
        var mainQueue = store.GetActiveBucket()!;
        store.SetBucketKind(mainQueue, "Replay");
        store.AddSlip(mainQueue, "main final value", "copy");
        store.CreateProject("Alternate", ["Queue"], "Queue", shifted: true);
        var alternateQueue = store.GetActiveBucket(true)!;
        store.SetBucketKind(alternateQueue, "Replay");
        store.AddSlip(alternateQueue, "alternate final value", "copy");
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var dispatcher = new QueuingDispatcher();
        var restoreDelay = new FirstWaitManualDelay();
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            notifications,
            out var keyboard,
            out _,
            delay: restoreDelay,
            dispatcher: dispatcher);

        AssertTrue(
            coordinator.OnTapDispatched(ShortcutContext(VK_V)),
            "Main Replay should handle its final tap.");
        AssertTrue(
            dispatcher.RunUntil(
                () => mainQueue.Slips.Count == 0 && keyboard.PasteCount == 1,
                TimeSpan.FromSeconds(5)),
            "Main Replay should enter its delayed final restoration.");
        AssertEqual("Replay", mainQueue.Settings.Kind, "Main should remain in its restoring state.");

        AssertTrue(
            coordinator.OnTapDispatched(ShortcutContext(VK_V, shifted: true)),
            "Alternate Replay should still handle a tap while Main restores.");
        AssertTrue(
            dispatcher.RunUntil(
                () => alternateQueue.Settings.Kind == "Standard"
                    && keyboard.PasteCount == 2,
                TimeSpan.FromSeconds(5)),
            "Alternate should safely paste and finalize without waiting for Main's restore delay.");
        AssertEqual("Replay", mainQueue.Settings.Kind, "Alternate completion must not finalize Main early.");
        AssertTrue(
            coordinator.OnTapDispatched(ShortcutContext(VK_V)),
            "Main taps should remain suppressed while only Main is restoring.");
        dispatcher.RunAll();
        AssertEqual(2, keyboard.PasteCount, "The suppressed Main tap must not duplicate either lane's final item.");

        restoreDelay.ReleaseFirst();
        AssertTrue(
            dispatcher.RunUntil(
                () => mainQueue.Settings.Kind == "Standard",
                TimeSpan.FromSeconds(5)),
            "Main should finalize once its own restore delay settles.");
        AssertEqual("user clipboard", clipboard.Text, "The lanes should converge on the original user clipboard.");
    }

    [Fact(DisplayName = "Runtime Replay final restore failures complete visibly")]
    public static void RuntimeReplayFinalRestoreFailureCompletesVisibly()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "final queued value", "copy");
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var dispatcher = new QueuingDispatcher();
        var restoreDelay = new ManualDelay();
        var notifications = new FakeNotificationSink();
        var logMessages = new List<string>();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            notifications,
            out var keyboard,
            out _,
            delay: restoreDelay,
            dispatcher: dispatcher,
            log: logMessages.Add);

        AssertTrue(
            coordinator.OnTapDispatched(ShortcutContext(VK_V)),
            "Replay should handle the final item before the injected restore failure.");
        AssertTrue(
            dispatcher.RunUntil(
                () => keyboard.PasteCount == 1 && queue.Slips.Count == 0,
                TimeSpan.FromSeconds(5)),
            "Replay should reach final restoration before failure injection.");
        clipboard.WriteResultOverride = new(
            ZetlClipboardWriteStatus.WriteFailedRestoreFailed,
            FailureReason: "injected final restore failure");

        restoreDelay.Release();
        AssertTrue(
            dispatcher.RunUntil(
                () => queue.Settings.Kind == "Standard"
                    && notifications.Messages.Exists(message =>
                        message.Contains("clipboard may have changed", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(5)),
            "A failed restore should still settle Replay with a visible integrity warning.");
        AssertTrue(
            logMessages.Exists(message =>
                message.Contains("clipboard integrity is uncertain", StringComparison.OrdinalIgnoreCase)),
            "A failed final restore should also leave a diagnostic log entry.");
    }

    [Fact(DisplayName = "Runtime Shift-lane Replay tap consumes a shifted paste chord")]
    public static void RuntimeShiftLaneReplayTapConsumesShiftedPaste()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo Shift", ["Queue"], "Queue", shifted: true);
        var queue = store.GetActiveBucket(true)!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "queued value", "copy");
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out var keyboard,
            out _);

        // Ctrl+Shift+V replays its Shift on pass-through, so the tap context
        // arrives with ReplayShift set; the Replay tap must still consume it.
        var handled = coordinator.OnTapDispatched(
            ShortcutContext(VK_V, shifted: true, replayShift: true));

        AssertTrue(handled, "Shift-lane Replay tap should suppress the physical paste.");
        AssertEqual(1, keyboard.PasteCount, "Shift-lane Replay tap should send one synthetic paste.");
        AssertEqual(0, queue.Slips.Count, "Shift-lane Replay should consume the queued note.");
    }

    [Fact(DisplayName = "Runtime Replay resumes clipboard when enabled")]
    public static void RuntimeReplayResumesClipboardWhenEnabled()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "queued value", "copy");
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out var keyboard,
            out var undo,
            replayResumeClipboard: true);

        var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertTrue(handled, "Replay tap should suppress the physical paste.");
        AssertEqual("user clipboard", clipboard.Text, "Replay should restore the user's clipboard when setting is enabled.");
    }

    [Fact(DisplayName = "Runtime Replay keeps last paste when disabled")]
    public static void RuntimeReplayKeepsLastPasteWhenDisabled()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "queued value", "copy");
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out var keyboard,
            out var undo,
            replayResumeClipboard: false);

        var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertTrue(handled, "Replay tap should suppress the physical paste.");
        AssertEqual("queued value", clipboard.Text, "Replay should NOT restore the user's clipboard and keep the last paste when setting is disabled.");
    }

    [Fact(DisplayName = "Runtime Replay restores rich and mixed clipboard formats")]
    public static void RuntimeReplayRestoresRichAndMixedClipboardFormats()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "queued value", "copy");
        var userImage = new ZetlClipboardImage([1, 2, 3, 4], 2, 2);
        var clipboard = new FakeClipboard(null, changeToken: 1);
        clipboard.SetMixedState(
            "formatted user text",
            "<p><strong>formatted</strong> user text</p>",
            userImage,
            changeToken: 1);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out var keyboard,
            out _);

        var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertTrue(handled, "Replay tap should suppress the physical paste.");
        AssertEqual(1, keyboard.PasteCount, "Replay should paste the queued item once.");
        AssertEqual("formatted user text", clipboard.Text, "Replay should restore the plain-text format.");
        AssertEqual(
            "<p><strong>formatted</strong> user text</p>",
            clipboard.RichHtml,
            "Replay should restore the rich HTML format.");
        AssertTrue(
            clipboard.Image?.PngBytes.SequenceEqual(userImage.PngBytes) == true,
            "Replay should restore an image format carried alongside text.");
        AssertEqual(1, clipboard.BackupRestoreCount, "Replay should perform one complete clipboard restore.");
        AssertEqual(0, queue.Slips.Count, "A successfully restored Replay should consume its item.");
    }

    [Fact(DisplayName = "Runtime Replay still pastes when the clipboard can't be backed up")]
    public static void RuntimeReplayPastesWhenClipboardCannotBeBackedUp()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "first value", "copy");
        store.AddSlip(queue, "second value", "copy");
        // A browser image copy can carry a virtual-file format Windows will
        // not hand over, so the user's clipboard cannot be backed up.
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1)
        {
            BackupFailureReason = "clipboard format FileContents could not be read"
        };
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            notifications,
            out var keyboard,
            out _);

        AssertTrue(coordinator.OnTapDispatched(ShortcutContext(VK_V)), "The Replay tap replaces the physical paste.");
        AssertEqual(1, keyboard.PasteCount, "Replay must still paste its item when the backup is impossible.");
        AssertEqual("first value", clipboard.Text, "The pasted item stays on the clipboard; there is nothing to restore.");
        AssertEqual("second value", queue.Slips.Single().Text, "The pasted item is consumed.");
        AssertTrue(
            notifications.Messages.Last().Contains("can't be restored", StringComparison.OrdinalIgnoreCase),
            "Replay says the previous clipboard won't come back.");

        coordinator.OnTapDispatched(ShortcutContext(VK_V));
        AssertEqual(2, keyboard.PasteCount, "Later taps keep pasting.");
        AssertEqual(0, queue.Slips.Count, "The queue drains normally.");
        AssertEqual("second value", clipboard.Text, "The last item is left on the clipboard.");
    }

    [Fact(DisplayName = "Runtime Replay does not paste after transactional staging fails")]
    public static void RuntimeReplayDoesNotPasteAfterTransactionalStageFailure()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "queued value", "copy");
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1)
        {
            WriteResultOverride = new(
                ZetlClipboardWriteStatus.WriteFailedRolledBack,
                FailureReason: "injected target format failure")
        };
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            notifications,
            out var keyboard,
            out var undo);

        var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertTrue(handled, "Replay should still suppress the physical paste.");
        AssertEqual(0, keyboard.PasteCount, "Replay must not inject paste after target staging fails.");
        AssertEqual(1, queue.Slips.Count, "The queued item must remain after staging rollback.");
        AssertFalse(undo.TryPop(false, out _), "A failed stage must not create an undo entry.");
        AssertTrue(
            notifications.Messages.Exists(message =>
                message.Contains("clipboard was preserved", StringComparison.OrdinalIgnoreCase)),
            "Replay should report that transactional rollback preserved the clipboard.");
    }

    [Fact(DisplayName = "Runtime Replay does not overwrite a newer matching clipboard")]
    public static void RuntimeReplayDoesNotOverwriteNewerMatchingClipboard()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "queued value", "copy");
        var clipboard = new FakeClipboard("original clipboard", changeToken: 1);
        var dispatcher = new QueuingDispatcher();
        var restoreDelay = new ManualDelay();
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            notifications,
            out var keyboard,
            out _,
            delay: restoreDelay,
            dispatcher: dispatcher);

        AssertTrue(
            coordinator.OnTapDispatched(ShortcutContext(VK_V)),
            "Replay tap should be handled.");
        AssertTrue(
            dispatcher.RunUntil(
                () => keyboard.PasteCount == 1 && queue.Slips.Count == 0,
                TimeSpan.FromSeconds(5)),
            "Replay should stage, paste, and consume before the delayed restore.");

        // The user copies richer content with the same visible text as the
        // Replay item while the restore delay is pending.
        AssertTrue(
            clipboard.SetRichText("queued value", "<p><em>new user copy</em></p>"),
            "The simulated newer clipboard write should succeed.");
        restoreDelay.Release();
        AssertTrue(
            SpinWait.SpinUntil(() => dispatcher.PendingCount > 0, TimeSpan.FromSeconds(5)),
            "The delayed restore should return to the dispatcher.");
        AssertTrue(
            dispatcher.RunUntil(
                () => queue.Settings.Kind == "Standard"
                    && notifications.Messages.Exists(message =>
                        message.Contains("newer clipboard", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(5)),
            "Replay should settle as complete after detecting newer clipboard ownership.");

        AssertEqual("queued value", clipboard.Text, "Replay should leave the newer clipboard text intact.");
        AssertEqual(
            "<p><em>new user copy</em></p>",
            clipboard.RichHtml,
            "Replay should not overwrite newer rich formats just because visible text matches.");
        AssertEqual(0, clipboard.BackupRestoreCount, "A changed clipboard token must cancel restoration.");
    }

    [Fact(DisplayName = "Runtime Replay handles images and restores image clipboard")]
    public static void RuntimeReplayHandlesImagesAndRestoresImageClipboard()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        var queuedBytes = new byte[] { 4, 5, 6 };
        store.AddImageSlip(
            project,
            queue,
            new ZetlClipboardImage(queuedBytes, 3, 2),
            "copy");
        var userBytes = new byte[] { 1, 2, 3 };
        var clipboard = new FakeClipboard(null, changeToken: 1)
        {
            Image = new ZetlClipboardImage(userBytes, 1, 1)
        };
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out var keyboard,
            out var undo);

        var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertTrue(handled, "Image Replay should suppress the physical paste.");
        AssertEqual(1, keyboard.PasteCount, "Image Replay should send one synthetic paste.");
        AssertEqual(2, clipboard.ImageSetCount, "Image Replay should inject the item and restore the prior image.");
        AssertTrue(
            clipboard.Image?.PngBytes.SequenceEqual(userBytes) == true,
            "Image Replay should restore the user's previous image clipboard.");
        AssertEqual(0, queue.Slips.Count, "Successful image Replay should consume the queued slip.");
        var review = project.Buckets.Single(bucket => bucket.Id == queue.Settings.ReplayReviewBucketId);
        AssertTrue(review.Slips.Single().IsImage, "Replay review should preserve the image slip type.");
        AssertEqual(
            queuedBytes.Length,
            store.ReadImageAsset(project, review.Slips.Single())?.Length,
            "Replay review should retain the queued image asset.");
        AssertTrue(undo.TryPop(false, out var action), "Image Replay should be undoable.");
        action!.Undo();
        AssertTrue(queue.Slips.Single().IsImage, "Undo should restore the image slip to the Replay queue.");
    }

    [Fact(DisplayName = "Runtime empty Replay reports a failed final paste")]
    public static void RuntimeEmptyReplayReportsFinalPasteFailure()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        // No notes: the empty-Replay tap does a final pass-through paste.
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var sink = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            sink,
            out var keyboard,
            out _);
        keyboard.PasteSucceeds = false;

        var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertTrue(handled, "An empty Replay tap is handled; it suppresses the physical paste.");
        AssertEqual(1, keyboard.PasteCount, "The final pass-through paste should be attempted.");
        AssertEqual("Standard", queue.Settings.Kind, "An empty Replay bucket returns to Standard.");
        AssertTrue(
            sink.Messages.Exists(message => message.Contains("didn't land", StringComparison.OrdinalIgnoreCase)),
            "A failed final paste should be reported, not silently called complete.");
    }

    [Fact(DisplayName = "Runtime Replay tap keeps the note when the paste fails")]
    public static void RuntimeReplayTapKeepsNoteWhenPasteFails()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "queued value", "copy");
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out var keyboard,
            out var undo);
        // Queueing succeeds but the worker reports the synthetic paste failed
        // (e.g. SendInput blocked by an elevated target).
        keyboard.PasteSucceeds = false;

        var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertTrue(handled, "Replay tap should still be handled even when the paste fails.");
        AssertEqual(1, keyboard.PasteCount, "A paste should have been attempted.");
        AssertEqual(1, queue.Slips.Count, "The note must be kept when the synthetic paste was not accepted.");
        AssertFalse(undo.TryPop(false, out _), "A failed paste should not push an undo entry.");
    }

    [Fact(DisplayName = "Runtime Replay tap defers clipboard work off the hook")]
    public static void RuntimeReplayTapDefersClipboardWorkOffHook()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        store.AddSlip(queue, "queued value", "copy");
        var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
        var dispatcher = new QueuingDispatcher();
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            notifications,
            out var keyboard,
            out _,
            dispatcher: dispatcher);

        var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

        // The decision returns synchronously, but none of the replay
        // clipboard/paste work runs inline -- it is queued on the dispatcher,
        // standing in for the move off the keyboard hook thread.
        AssertTrue(handled, "Replay tap should be handled synchronously.");
        AssertTrue(dispatcher.PendingCount > 0, "Replay work should be enqueued, not run on the hook thread.");
        AssertEqual(0, keyboard.PasteCount, "No paste should be sent before the queued work runs.");
        AssertEqual(1, queue.Slips.Count, "The queued note should not be consumed inline.");

        AssertTrue(
            dispatcher.RunUntil(
                () => keyboard.PasteCount == 1
                    && queue.Slips.Count == 0
                    && queue.Settings.Kind == "Standard"
                    && notifications.Messages.Exists(message =>
                        message.Contains("replay complete", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(5)),
            "Running queued work should paste, consume, restore, and finalize Replay off the hook thread.");
    }
}
