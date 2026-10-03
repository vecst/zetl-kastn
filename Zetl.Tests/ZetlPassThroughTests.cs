using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Tests.ZetlTestSupport;
using Xunit;
using System;
using System.Collections.Generic;

namespace ZETL.Tests;

public class ZetlPassThroughTests
{
    [Fact(DisplayName = "Zetl state passes through only the latest automatic copy")]
    public static void StatePassesThroughOnlyTheLatestAutomaticCopy()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Inbox"], "Inbox");
        var bucket = store.ActiveBucket!;
        store.AddSlip(bucket, "alpha", ZetlStateStore.AutoCopySource);
        store.AddSlip(bucket, "beta", ZetlStateStore.AutoCopySource);
        AssertFalse(PassThrough(store, "alpha"), "Only the latest automatic copy passes through.");
        AssertTrue(PassThrough(store, "beta"), "Pasting the latest automatic copy passes it through.");
        AssertEqual("alpha", bucket.Slips.Single().Text, "The earlier copy stays.");

        store.AddSlip(bucket, "held", "copy");
        AssertFalse(PassThrough(store, "held"), "A held capture never passes through.");
        AssertTrue(
            store.TryPassThroughLatestCopy(
                "alpha",
                null,
                shifted: false,
                out var passedBucket,
                out var passedSlip,
                out var reviewBucket,
                out var reviewSlip),
            "Behind a held capture, alpha is still the latest automatic copy.");
        AssertEqual(bucket.Id, passedBucket?.Id, "Pass-through reports the source bucket.");
        AssertEqual("alpha", passedSlip?.Text, "Pass-through reports the slip it set aside.");
        AssertEqual(ZetlStateStore.PassedThroughBucketName, reviewBucket?.Name, "Set aside in Passed Through.");
        store.RestorePassedThroughSlip(passedBucket!, passedSlip!, reviewBucket, reviewSlip?.Id);
        AssertEqual(2, bucket.Slips.Count, "Undo puts the copy back beside the held capture.");
        AssertEqual(1, reviewBucket!.Slips.Count, "Undo takes it back out of Passed Through.");
    }

    [Fact(DisplayName = "Zetl state pass-through recovers text across restart")]
    public static void StatePassThroughRecoversTextAcrossRestart()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        var source = store.ActiveBucket!;
        store.AddSlip(source, "durable text", ZetlStateStore.AutoCopySource);

        AssertTrue(
            store.TryPassThroughLatestCopy("durable text", null, false, out _, out _, out var review, out _),
            "Passing text through should set the slip aside.");
        AssertEqual(ZetlStateStore.PassedThroughBucketName, review?.Name, "Set aside in Passed Through.");

        var reloaded = new ZetlStateStore(temp.Path);
        var loadedProject = reloaded.State.Projects.Single(item => item.Id == project.Id);
        var loadedSource = loadedProject.Buckets.Single(item => item.Id == source.Id);
        var loadedReview = loadedProject.Buckets.Single(item => item.Id == loadedSource.Settings.PassThroughReviewBucketId);
        AssertEqual(0, loadedSource.Slips.Count, "The source should remain consumed after restart.");
        AssertEqual("durable text", loadedReview.Slips.Single().Text, "Passed-through text should remain recoverable after restart.");
        AssertEqual("passed-through", loadedReview.Slips.Single().Source, "Set-aside slips say they passed through.");
    }

    [Fact(DisplayName = "Zetl state pass-through recovers images across restart")]
    public static void StatePassThroughRecoversImageAcrossRestart()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        var source = store.ActiveBucket!;
        var bytes = new byte[] { 8, 6, 7, 5, 3, 0, 9 };
        var image = store.AddImageSlip(
            project,
            source,
            new ZetlClipboardImage(bytes, 7, 1),
            ZetlStateStore.AutoCopySource);

        AssertTrue(
            store.TryPassThroughLatestCopy(null, image.Image!.Sha256, false, out _, out _, out _, out _),
            "Passing an image through should set the slip aside.");

        var reloaded = new ZetlStateStore(temp.Path);
        var loadedProject = reloaded.State.Projects.Single(item => item.Id == project.Id);
        var loadedSource = loadedProject.Buckets.Single(item => item.Id == source.Id);
        var recovered = loadedProject.Buckets
            .Single(item => item.Id == loadedSource.Settings.PassThroughReviewBucketId)
            .Slips.Single();
        AssertTrue(recovered.IsImage, "Passed-through image type should survive restart.");
        AssertEqual(7, recovered.Image?.Width, "Passed-through image metadata should survive restart.");
        AssertEqual(bytes.Length, reloaded.ReadImageAsset(loadedProject, recovered)?.Length, "Passed-through image bytes should remain readable after restart.");
    }

    [Fact(DisplayName = "Zetl state pass-through recovers mixed slips across restart")]
    public static void StatePassThroughRecoversMixedSlipAcrossRestart()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        var source = store.ActiveBucket!;
        var bytes = new byte[] { 1, 3, 3, 7 };
        var html = "<p><strong>mixed</strong></p>";
        var mixed = store.AddImageSlip(
            project,
            source,
            new ZetlClipboardImage(bytes, 2, 2),
            ZetlStateStore.AutoCopySource,
            caption: "mixed",
            preferTextContent: true,
            richHtml: html,
            replayFormats: [new ZetlClipboardFormatData(42, [4, 2], "Native Test")]);
        store.UpdateSlip(
            mixed,
            mixed.Text,
            align: "right",
            bold: true,
            fontFamily: "Aptos",
            fontSize: 18,
            textColor: "#cc0000");

        AssertTrue(
            store.TryPassThroughLatestCopy(null, mixed.Image!.Sha256, false, out _, out _, out _, out _),
            "Passing a mixed slip through should set the complete slip aside.");

        var reloaded = new ZetlStateStore(temp.Path);
        var loadedProject = reloaded.State.Projects.Single(item => item.Id == project.Id);
        var loadedSource = loadedProject.Buckets.Single(item => item.Id == source.Id);
        var recovered = loadedProject.Buckets
            .Single(item => item.Id == loadedSource.Settings.PassThroughReviewBucketId)
            .Slips.Single();
        AssertEqual("mixed", recovered.Text, "Mixed pass-through should retain text after restart.");
        AssertTrue(recovered.Image is not null, "Mixed pass-through should retain its attached image after restart.");
        AssertEqual(html, recovered.RichHtml, "Mixed pass-through should retain rich clipboard content after restart.");
        AssertEqual("Native Test", recovered.ReplayFormats?.Single().RegisteredName, "Mixed pass-through should retain native replay formats after restart.");
        AssertEqual("right", recovered.Align, "Mixed pass-through should retain block alignment after restart.");
        AssertTrue(recovered.Bold, "Mixed pass-through should retain emphasis after restart.");
        AssertEqual("Aptos", recovered.FontFamily, "Mixed pass-through should retain its font after restart.");
        AssertEqual(18, recovered.FontSize, "Mixed pass-through should retain its font size after restart.");
        AssertEqual("#CC0000", recovered.TextColor, "Mixed pass-through should retain its normalized color after restart.");
        AssertEqual(bytes.Length, reloaded.ReadImageAsset(loadedProject, recovered)?.Length, "Mixed pass-through should retain readable image bytes after restart.");
    }

    [Fact(DisplayName = "Zetl state never passes a Replay item through")]
    public static void StateNeverPassesReplayItemsThrough()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.ActiveBucket!;
        store.AddSlip(queue, "queued", ZetlStateStore.AutoCopySource);
        store.SetBucketKind(queue, "Replay");

        AssertFalse(PassThrough(store, "queued"), "Replay owns its queue; pass-through leaves it alone.");
        AssertEqual(1, queue.Slips.Count, "The queued item stays.");
    }

    [Fact(DisplayName = "Runtime pass-through sets aside a dual slip by image hash")]
    public static void RuntimePassThroughSetsAsideDualSlipByImageHash()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        var bucket = store.GetActiveBucket()!;
        var bytes = new byte[] { 3, 1, 4 };
        store.AddImageSlip(
            project,
            bucket,
            new ZetlClipboardImage(bytes, 3, 1),
            ZetlStateStore.AutoCopySource,
            caption: "A1\tB1",
            preferTextContent: true);
        // The paste re-offers both formats, exactly as the original copy did.
        var clipboard = new FakeClipboard("A1\tB1", changeToken: 1)
        {
            Image = new ZetlClipboardImage(bytes, 3, 1)
        };
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out _,
            out var undo,
            passThrough: true);

        var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertFalse(handled, "Pass-through lets the physical paste go through.");
        AssertEqual(0, bucket.Slips.Count, "The matching dual slip passes through on its image hash.");
        AssertTrue(undo.TryPop(false, out _), "Passing it through is undoable.");
    }

    [Fact(DisplayName = "Runtime pass-through sets aside a copy pasted straight away")]
    public static void RuntimePassThroughSetsAsidePastedCopy()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Inbox"], "Inbox");
        var bucket = store.GetActiveBucket()!;
        store.AddSlip(bucket, "paste once", ZetlStateStore.AutoCopySource);
        var clipboard = new FakeClipboard("paste once", changeToken: 1);
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            notifications,
            out _,
            out var undo,
            passThrough: true);

        var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertFalse(handled, "The physical paste goes through.");
        AssertEqual(0, bucket.Slips.Count, "The pasted copy doesn't stay in the bucket.");
        AssertEqual(
            ZetlStateStore.PassedThroughBucketName,
            store.ActiveProject!.Buckets.Single(item => item.Slips.Count == 1).Name,
            "It's set aside in Passed Through.");
        AssertEqual(
            "Passed through, not kept. Hold Ctrl+Z to keep it.",
            notifications.Messages.Single(),
            "The toast says how to keep it.");
        AssertTrue(undo.TryPop(false, out var action), "Passing it through is undoable.");
        AssertEqual("Kept the pasted copy in Inbox.", action?.Message, "Undo says where the copy went back.");
        action!.Undo();
        AssertEqual("paste once", bucket.Slips.Single().Text, "Undo keeps the copy.");
    }

    [Fact(DisplayName = "Runtime held Ctrl+P flips pass-through until ten quiet minutes pass")]
    public static async Task RuntimeHeldCtrlPFlipsPassThroughForNow()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Inbox"], "Inbox");
        var bucket = store.GetActiveBucket()!;
        store.AddSlip(bucket, "work quote", ZetlStateStore.AutoCopySource);
        var clipboard = new FakeClipboard("work quote", changeToken: 1);
        var notifications = new FakeNotificationSink();
        var now = DateTimeOffset.Parse("2026-10-02T12:00:00Z");
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            notifications,
            out _,
            out _,
            clock: () => now);
        var changes = 0;
        coordinator.Inner.PassThroughChanged += () => changes++;

        coordinator.OnTapDispatched(ShortcutContext(VK_V));
        AssertEqual(1, bucket.Slips.Count, "With the setting off, a pasted copy stays.");

        await coordinator.HandleHoldAsync(ShortcutContext(VK_P));
        AssertTrue(coordinator.Inner.IsPassThroughOn(false), "A held Ctrl+P turns it on.");
        AssertTrue(coordinator.Inner.IsPassThroughFlipped(false), "...for now, not as the setting.");
        AssertTrue(
            notifications.Messages.Last().StartsWith("Pass-through on for now", StringComparison.Ordinal),
            "The toast says it's for now.");
        AssertFalse(coordinator.Inner.IsPassThroughOn(true), "The Shift lane keeps its own state.");

        store.AddSlip(bucket, "link for a friend", ZetlStateStore.AutoCopySource);
        clipboard.SetState("link for a friend", changeToken: 2);
        coordinator.OnTapDispatched(ShortcutContext(VK_V));
        AssertEqual("work quote", bucket.Slips.Single().Text, "The friend's link passes through.");

        now += TimeSpan.FromMinutes(9);
        coordinator.OnTapDispatched(ShortcutContext(VK_V));
        now += TimeSpan.FromMinutes(9);
        AssertTrue(coordinator.Inner.IsPassThroughOn(false), "Each paste keeps the flip alive.");

        now += TimeSpan.FromMinutes(1);
        AssertFalse(coordinator.Inner.IsPassThroughOn(false), "Ten quiet minutes end it.");
        var before = changes;
        coordinator.Inner.ExpirePassThroughFlips();
        AssertEqual(before + 1, changes, "Expiring it tells the tray.");

        await coordinator.HandleHoldAsync(ShortcutContext(VK_P));
        await coordinator.HandleHoldAsync(ShortcutContext(VK_P));
        AssertFalse(coordinator.Inner.IsPassThroughFlipped(false), "Holding Ctrl+P again goes back to the setting.");
        AssertTrue(
            notifications.Messages.Last().StartsWith("Pass-through off:", StringComparison.Ordinal),
            "Back to the setting isn't 'for now'.");

        await coordinator.HandleHoldAsync(ShortcutContext(VK_P));
        store.CreateProject("Other", ["Inbox"], "Inbox");
        AssertFalse(coordinator.Inner.IsPassThroughFlipped(false), "Switching projects ends the flip.");
    }

    [Fact(DisplayName = "Runtime pass-through sets aside a pasted image")]
    public static void RuntimePassThroughSetsAsidePastedImage()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        var bucket = store.GetActiveBucket()!;
        var bytes = new byte[] { 3, 1, 4, 1, 5 };
        store.AddImageSlip(
            project,
            bucket,
            new ZetlClipboardImage(bytes, 5, 1),
            ZetlStateStore.AutoCopySource);
        var clipboard = new FakeClipboard(null, changeToken: 1)
        {
            Image = new ZetlClipboardImage(bytes, 5, 1)
        };
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out _,
            out var undo,
            passThrough: true);

        var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertFalse(handled, "The physical paste goes through.");
        AssertEqual(0, bucket.Slips.Count, "The pasted image passes through.");
        AssertTrue(undo.TryPop(false, out var action), "Passed-through image should be undoable.");
        action!.Undo();
        AssertTrue(bucket.Slips.Single().IsImage, "Undo should restore the image slip.");
    }
}
