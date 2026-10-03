using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Tests.ZetlTestSupport;
using Xunit;
using System;
using System.Collections.Generic;

namespace ZETL.Tests;

public class ZetlRuntimeCaptureTests
{
    [Fact(DisplayName = "Runtime auto-captures copied text")]
    public static async Task RuntimeAutoCapturesCopiedText()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        var clipboard = new FakeClipboard(" copied text ", changeToken: 2);
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            notifications,
            out _,
            out _);

        var origin = ZetlCaptureOrigin.Create(
            "Editor",
            "editor",
            "Draft",
            ZetlCaptureOriginDetail.ApplicationAndWindowTitle);
        await coordinator.OnPhysicalShortcutPassedThroughAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1),
            origin);

        var note = store.GetActiveBucket()!.Slips.Single();
        AssertEqual("copied text", note.Text, "Auto-capture should trim and save copied text.");
        AssertEqual(ZetlStateStore.AutoCopySource, note.Source, "Auto-capture marks the copy as automatic, so pass-through can tell it from a held capture.");
        AssertEqual("Editor", note.CaptureOrigin?.ApplicationName, "Auto-capture should retain its keydown origin.");
        AssertEqual(
            "Captured to Inbox in Demo.",
            notifications.Messages.Single(),
            "Auto-capture should report its destination.");
        AssertEqual(project.Id, store.GetActiveProject()!.Id, "Auto-capture should keep the active project.");
    }

    [Fact(DisplayName = "Runtime tapped copy with no project saves nothing when idle capture is off")]
    public static async Task RuntimeTappedCopyWithIdleCaptureOffSavesNothing()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard("copied text", changeToken: 2),
            notifications,
            out _,
            out _);

        await coordinator.OnPhysicalShortcutPassedThroughAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1),
            captureOrigin: null);

        AssertEqual(
            0,
            store.State.Projects.Sum(project => project.Buckets.Sum(bucket => bucket.Slips.Count)),
            "With idle capture off, a tapped copy with no active project saves nothing.");
        AssertFalse(notifications.Messages.Any(), "A plain copy should not toast.");
        AssertTrue(store.GetActiveProject() is null, "A plain copy activates nothing.");
    }

    [Fact(DisplayName = "Runtime tapped copy with no project captures to the Journal when set")]
    public static async Task RuntimeTappedCopyWithIdleCaptureJournalCapturesToJournal()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path)
        {
            Defaults = ZetlBucketDefaults.Standard with { IdleCopyCapture = ZetlIdleCopyCapture.Journal }
        };
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard("copied text", changeToken: 2),
            new FakeNotificationSink(),
            out _,
            out _);

        await coordinator.OnPhysicalShortcutPassedThroughAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1),
            captureOrigin: null);

        var journal = store.State.Projects.Single(project => project.JournalMode);
        var capture = journal.Buckets.Single(bucket =>
            bucket.Name == ZetlStateStore.JournalCaptureBucketName);
        AssertEqual("copied text", capture.Slips.Single().Text, "The copy lands in today's Journal Capture bucket.");
        AssertTrue(store.GetActiveProject() is null, "Capturing to the Journal while idle does not activate it.");
    }

    [Fact(DisplayName = "Runtime tapped copies count as activity and auto-return still applies")]
    public static async Task RuntimeTappedCopiesCountTowardAutoReturn()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path)
        {
            Defaults = ZetlBucketDefaults.Standard with { JournalAutoReturnHours = 2 }
        };
        var work = store.CreateProject("Work", ["Inbox"], "Inbox");
        var clipboard = new FakeClipboard("first", changeToken: 2);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out _,
            out _);

        work.LastActiveUtc = DateTime.UtcNow.AddHours(-1);
        await coordinator.OnPhysicalShortcutPassedThroughAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1),
            captureOrigin: null);
        AssertTrue(
            DateTime.UtcNow - work.LastActiveUtc < TimeSpan.FromMinutes(1),
            "A tapped capture refreshes the project's auto-return window.");
        AssertEqual(work.Id, store.GetActiveProject()?.Id, "An active project inside its window keeps capture.");

        work.LastActiveUtc = DateTime.UtcNow.AddHours(-3);
        clipboard.SetState("second", changeToken: 4);
        await coordinator.OnPhysicalShortcutPassedThroughAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 3),
            captureOrigin: null);
        AssertTrue(store.GetActiveProject() is null, "A tapped copy past the quiet window switches the project off.");
        AssertEqual(
            1,
            work.Buckets.Sum(bucket => bucket.Slips.Count),
            "The late copy is not filed into the quiet project.");
    }

    [Fact(DisplayName = "Runtime Esc on an empty held copy leaves the lane idle for a quick note")]
    public static async Task RuntimeEmptyHeldCopyThenQuickNoteLeavesLaneIdle()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard(null, changeToken: 1),
            new FakeNotificationSink(),
            out _,
            out _,
            quickNoteToClipboard: false);

        var board = await coordinator.HandleHoldAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1));
        AssertTrue(board is ZetlBoardRequest, "A held copy with nothing selected opens the Board.");
        AssertTrue(store.GetActiveProject() is null, "Opening the Board from a held copy activates nothing.");

        var quick = (ZetlNoteCaptureRequest)(await coordinator.HandleHoldAsync(
            ShortcutContext(VK_X, clipboardSequenceNumber: 1)))!;
        AssertFalse(quick.ProjectWasActive, "The quick note sees no active project.");
        coordinator.CompleteNoteCapture(quick, SavedNote(quick, "jot", startProject: false));
        AssertTrue(store.GetActiveProject() is null, "The quick note leaves the lane idle.");
    }

    [Fact(DisplayName = "Runtime held copy saved with Activate keeps the Journal active through a quick note")]
    public static async Task RuntimeHeldCopySaveKeepsJournalActiveThroughQuickNote()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard("copied", changeToken: 2),
            new FakeNotificationSink(),
            out _,
            out _,
            quickNoteToClipboard: false);

        var copy = (ZetlNoteCaptureRequest)(await coordinator.HandleHoldAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1)))!;
        AssertTrue(copy.Project.JournalMode, "With nothing active, a held copy files to the Journal.");
        AssertFalse(copy.ProjectWasActive, "The Journal was not active when the gesture began.");
        AssertTrue(copy.StartProjectDefault, "A held copy starts with Activate on.");
        AssertTrue(store.GetActiveProject() is null, "Opening the capture dialog activates nothing.");

        coordinator.CompleteNoteCapture(copy, SavedNote(copy, "copied", startProject: true));
        AssertEqual(copy.Project.Id, store.GetActiveProject()?.Id, "Saving with Activate on activates the Journal.");

        var discarded = (ZetlNoteCaptureRequest)(await coordinator.HandleHoldAsync(
            ShortcutContext(VK_X, clipboardSequenceNumber: 3)))!;
        coordinator.CompleteNoteCapture(
            discarded,
            SavedNote(discarded, "", startProject: false) with { Committed = false });
        AssertEqual(copy.Project.Id, store.GetActiveProject()?.Id, "Esc on a quick note changes nothing.");

        var quick = (ZetlNoteCaptureRequest)(await coordinator.HandleHoldAsync(
            ShortcutContext(VK_X, clipboardSequenceNumber: 3)))!;
        AssertTrue(quick.ProjectWasActive, "The quick note sees the Journal active, so its toggle starts on.");
        coordinator.CompleteNoteCapture(quick, SavedNote(quick, "jot", startProject: true));
        AssertEqual(copy.Project.Id, store.GetActiveProject()?.Id, "The quick note keeps the Journal active.");
    }

    [Fact(DisplayName = "Runtime quick note keeps the active project active")]
    public static async Task RuntimeQuickNoteKeepsActiveProject()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var work = store.CreateProject("Work", ["Inbox"], "Inbox");
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard(null, changeToken: 1),
            new FakeNotificationSink(),
            out _,
            out _,
            quickNoteToClipboard: false);

        var quick = (ZetlNoteCaptureRequest)(await coordinator.HandleHoldAsync(
            ShortcutContext(VK_X, clipboardSequenceNumber: 1)))!;
        AssertEqual(work.Id, quick.Project.Id, "A quick note files to the active project.");
        AssertTrue(quick.ProjectWasActive, "The quick note's Activate toggle starts on.");
        coordinator.CompleteNoteCapture(quick, SavedNote(quick, "jot", startProject: true));
        AssertEqual(work.Id, store.GetActiveProject()?.Id, "The quick note keeps the project active.");
    }

    [Fact(DisplayName = "Runtime tapped copies into a journal keep a user-selected bucket")]
    public static async Task RuntimeTappedCopiesKeepUserSelectedJournalBucket()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path)
        {
            Defaults = ZetlBucketDefaults.Standard with { IdleCopyCapture = ZetlIdleCopyCapture.Journal }
        };
        var journal = store.GetCaptureHome();
        var math = store.AddBucket(journal, "Math");
        store.SetActiveBucket(journal, math.Id);
        var clipboard = new FakeClipboard("first", changeToken: 2);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out _,
            out _);

        await coordinator.OnPhysicalShortcutPassedThroughAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1),
            captureOrigin: null);
        AssertEqual("first", math.Slips.Single().Text, "A copy lands in the bucket the user selected.");

        // A held gesture looks up the capture home; it must not reset the choice.
        store.GetCaptureHome();
        clipboard.SetState("second", changeToken: 4);
        await coordinator.OnPhysicalShortcutPassedThroughAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 3),
            captureOrigin: null);
        AssertEqual(2, math.Slips.Count, "The selected bucket is remembered across captures.");
        AssertEqual(math.Id, journal.ActiveBucketId, "The selection stays active.");

        // Selecting one of today's managed buckets goes back to the daily roll.
        var today = store.EnsureJournalDayBucket(journal, DateTime.Now)!;
        store.SetActiveBucket(journal, today.Id);
        AssertEqual(
            ZetlStateStore.JournalCaptureBucketName,
            store.RollJournalBucket(journal, DateTime.Now)!.Name,
            "Choosing a day bucket returns copies to today's Capture.");
    }

    [Fact(DisplayName = "Runtime never captures a copy marked private by its app")]
    public static async Task RuntimeNeverCapturesPrivateCopies()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Research", ["Inbox"], "Inbox");
        var clipboard = new FakeClipboard("hunter2", changeToken: 2) { MarkedPrivate = true };
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            notifications,
            out _,
            out _);

        await coordinator.OnPhysicalShortcutPassedThroughAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1),
            captureOrigin: null);
        AssertEqual(0, project.Buckets.Sum(bucket => bucket.Slips.Count), "A tapped copy of a password is not captured.");
        AssertFalse(notifications.Messages.Any(), "A skipped private copy stays silent, like Windows' clipboard history.");

        var held = await coordinator.HandleHoldAsync(ShortcutContext(VK_C, clipboardSequenceNumber: 3));
        AssertTrue(held is null, "Holding Ctrl+C on a private copy opens nothing.");
        AssertTrue(
            notifications.Messages.Single().Contains("marked private", StringComparison.Ordinal),
            "A held copy explains why nothing was captured.");
        AssertEqual(0, project.Buckets.Sum(bucket => bucket.Slips.Count), "Nothing is saved either way.");
    }

    [Fact(DisplayName = "Runtime auto-captures and replays rich text")]
    public static async Task RuntimeAutoCapturesAndReplaysRichText()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        var richHtml =
            "<table><tr><td style=\"font-weight:bold;text-decoration:underline;text-align:right\">A1</td></tr></table>";
        var clipboard = new FakeClipboard(null, changeToken: 2);
        clipboard.SetMixedState("A1", richHtml, image: null, changeToken: 2);
        clipboard.NativeReplayFormats =
        [
            new ZetlClipboardFormatData(
                13,
                System.Text.Encoding.Unicode.GetBytes("A1\0")),
            new ZetlClipboardFormatData(
                50001,
                System.Text.Encoding.UTF8.GetBytes(richHtml),
                "HTML Format"),
            new ZetlClipboardFormatData(
                50002,
                [1, 2, 3, 4],
                "Star Embed Source (XML)"),
            new ZetlClipboardFormatData(
                50003,
                [5, 6, 7, 8],
                "Star Object Descriptor (XML)")
        ];
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out var keyboard,
            out _,
            replayResumeClipboard: false);

        await coordinator.OnPhysicalShortcutPassedThroughAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1),
            captureOrigin: null);

        var captured = queue.Slips.Single();
        AssertEqual(richHtml, captured.RichHtml, "Auto-capture should retain the source HTML fragment.");
        AssertTrue(
            captured.ReplayFormats?.Any(item =>
                item.RegisteredName == "Star Embed Source (XML)") == true,
            "Auto-capture should retain Calc's native source representation.");
        store.SetBucketKind(queue, "Replay");
        clipboard.SetState("ordinary user clipboard", changeToken: 3);

        var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

        AssertTrue(handled, "Rich Replay should suppress the physical paste.");
        AssertEqual(1, keyboard.PasteCount, "Rich Replay should send one synthetic paste.");
        AssertEqual("A1", clipboard.Text, "Rich Replay should keep the plain-text fallback.");
        AssertEqual(richHtml, clipboard.RichHtml, "Rich Replay should stage the captured HTML fragment.");
        AssertTrue(
            clipboard.LastRestoredRawFormats?.Any(item =>
                item.RegisteredName == "Star Embed Source (XML)") == true,
            "Rich Replay should prefer Calc's native representation over HTML import.");
        var review = store.GetActiveProject()!.Buckets
            .Single(bucket => bucket.Id == queue.Settings.ReplayReviewBucketId);
        AssertEqual(richHtml, review.Slips.Single().RichHtml, "Replay review should retain the rich representation.");
    }

    [Fact(DisplayName = "Runtime auto-captures copied images")]
    public static async Task RuntimeAutoCapturesCopiedImages()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        var clipboard = new FakeClipboard(null, changeToken: 2)
        {
            Image = new ZetlClipboardImage([1, 2, 3], 30, 20)
        };
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            notifications,
            out _,
            out _);

        await coordinator.OnPhysicalShortcutPassedThroughAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1),
            ZetlCaptureOrigin.Create(
                "Image Editor",
                "editor",
                "Canvas",
                ZetlCaptureOriginDetail.ApplicationAndWindowTitle));

        var note = store.GetActiveBucket()!.Slips.Single();
        AssertTrue(note.IsImage, "An image clipboard should create an image slip.");
        AssertEqual(30, note.Image?.Width, "Captured image width should persist.");
        AssertEqual("Canvas", note.CaptureOrigin?.WindowTitle, "Image capture should retain keydown provenance.");
        AssertEqual(1, store.GetProjectAssets(project).Count, "Image auto-capture should write one project asset.");
        AssertEqual(
            "Captured image to Inbox in Demo.",
            notifications.Messages.Single(),
            "Image capture should report its destination clearly.");
    }

    [Fact(DisplayName = "Runtime auto-captures dual text+image clipboards as text")]
    public static async Task RuntimeAutoCapturesDualClipboardAsText()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        // A spreadsheet copy: the clipboard carries the cell text and a
        // bitmap rendering at the same time.
        var richHtml = "<table><tr><td><strong>A1</strong></td><td>B1</td></tr></table>";
        var clipboard = new FakeClipboard(null, changeToken: 2);
        clipboard.SetMixedState(
            "A1\tB1\nA2\tB2",
            richHtml,
            new ZetlClipboardImage([1, 2, 3], 30, 20),
            changeToken: 2);
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            notifications,
            out _,
            out _);

        await coordinator.OnPhysicalShortcutPassedThroughAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1),
            captureOrigin: null);

        var note = store.GetActiveBucket()!.Slips.Single();
        AssertFalse(note.IsImage, "A dual capture should present as text, not as a picture.");
        AssertEqual("A1\tB1\nA2\tB2", note.Text, "A dual capture should keep the clipboard text as content.");
        AssertTrue(note.Image is not null, "A dual capture should retain the clipboard picture.");
        AssertEqual(richHtml, note.RichHtml, "A dual capture should retain the rich HTML representation.");
        AssertEqual(1, store.GetProjectAssets(project).Count, "A dual capture should write its picture asset.");
        AssertEqual(
            "Captured to Inbox in Demo.",
            notifications.Messages.Single(),
            "A dual capture should report as an ordinary text capture.");
    }

    [Fact(DisplayName = "Runtime clipboard capture retries a changed generation atomically")]
    public static async Task RuntimeClipboardCaptureRetriesChangedGeneration()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        var clipboard = new GenerationChangingClipboard();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out _,
            out _);

        await coordinator.OnPhysicalShortcutPassedThroughAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1),
            captureOrigin: null);

        var note = store.GetActiveBucket()!.Slips.Single();
        AssertTrue(
            clipboard.TextReadCount >= 2,
            "Capture should retry after the token changes between format reads.");
        AssertEqual("generation two", note.Text, "Capture must discard text from the superseded generation.");
        AssertEqual(
            "<p><strong>generation two</strong></p>",
            note.RichHtml,
            "HTML must come from the same generation as the committed text.");
        AssertTrue(note.Image is not null, "The coherent generation should retain its companion image.");
        AssertEqual(
            (byte)2,
            store.ReadImageAsset(project, note)?.Single(),
            "Image bytes must come from the same generation as the committed text.");
        AssertEqual(
            (byte)2,
            note.ReplayFormats?.Single().Data.Single(),
            "Native Replay data must come from the same generation as the committed text.");
    }

    [Fact(DisplayName = "Runtime clipboard capture refuses persistently unstable generations")]
    public static async Task RuntimeClipboardCaptureRefusesUnstableGenerations()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Inbox"], "Inbox");
        var clipboard = new GenerationChangingClipboard(changeEveryTextRead: true);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out _,
            out _);

        await coordinator.OnPhysicalShortcutPassedThroughAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1),
            captureOrigin: null);

        AssertEqual(
            0,
            store.GetActiveBucket()!.Slips.Count,
            "Auto-capture must commit nothing when no retry observes one complete generation.");
    }

    [Fact(DisplayName = "Dual slips survive persistence text-preferred")]
    public static void DualSlipSurvivesPersistence()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        var bucket = store.GetActiveBucket()!;
        store.AddImageSlip(
            project,
            bucket,
            new ZetlClipboardImage([9, 9, 9], 4, 4),
            "copy",
            caption: "A1\tB1",
            preferTextContent: true,
            richHtml: "<table><tr><td><strong>A1</strong></td><td>B1</td></tr></table>");

        var reloaded = new ZetlStateStore(temp.Path);

        var note = reloaded.State.Projects
            .Single(item => item.Name == "Demo")
            .Buckets.Single(item => item.Name == "Inbox")
            .Slips.Single();
        AssertFalse(note.IsImage, "A reloaded dual slip should stay text-preferred.");
        AssertEqual("A1\tB1", note.Text, "A reloaded dual slip should keep its text content.");
        AssertTrue(note.Image is not null, "A reloaded dual slip should keep its attached picture.");
        AssertTrue(
            note.RichHtml?.Contains("<strong>A1</strong>", StringComparison.Ordinal) == true,
            "A reloaded dual slip should keep its rich HTML representation.");
    }

    [Fact(DisplayName = "Runtime held copy opens image capture and saves captions")]
    public static async Task RuntimeHeldCopyCapturesImagesDirectly()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard(null, changeToken: 2),
            notifications,
            out _,
            out _);
        var context = ShortcutContext(VK_C, clipboardSequenceNumber: 1);
        var pending = new ZetlPendingShortcut(
            VK_C,
            shiftLane: false,
            clipboardSequenceNumber: 1,
            captureOrigin: ZetlCaptureOrigin.Create(
                "Snipping Tool",
                "snippingtool",
                "Screenshot",
                ZetlCaptureOriginDetail.ApplicationAndWindowTitle));
        pending.SetObservedClipboardContent(
            null,
            new ZetlClipboardImage([7, 8, 9], 3, 2));

        var request = await coordinator.HandleClaimedHoldAsync(context, pending);

        AssertTrue(request is ZetlNoteCaptureRequest, "Held image copy should open the shared capture dialog.");
        var capture = (ZetlNoteCaptureRequest)request!;
        AssertTrue(capture.Image is not null, "The capture request should carry the normalized image.");
        coordinator.CompleteNoteCapture(
            capture,
            new ZetlNoteCaptureResult(
                Committed: true,
                NoteText: "Annotated screenshot",
                StartProject: true,
                CreateNewProject: false,
                ProjectName: capture.Project.Name,
                SelectedBucketName: capture.PreferredBucket!.Name,
                SelectedBucket: capture.PreferredBucket));

        var project = store.GetActiveProject()!;
        var note = project.Buckets.SelectMany(bucket => bucket.Slips).Single();
        AssertTrue(note.IsImage, "Held image copy should create an image slip.");
        AssertEqual("Annotated screenshot", note.Text, "The image caption should be stored as slip text.");
        AssertEqual("Screenshot", note.CaptureOrigin?.WindowTitle, "Held image copy should retain its origin.");
        AssertTrue(
            notifications.Messages.Single().StartsWith("Saved image to", StringComparison.Ordinal),
            "Committed image capture should report its destination.");
    }

    [Fact(DisplayName = "Runtime held dual copy saves text-preferred with the picture")]
    public static async Task RuntimeHeldCopyDualSavesTextPreferred()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard(null, changeToken: 2),
            notifications,
            out _,
            out _);
        var pending = new ZetlPendingShortcut(
            VK_C,
            shiftLane: false,
            clipboardSequenceNumber: 1,
            captureOrigin: null);
        pending.SetObservedClipboardContent(
            "A1\tB1",
            new ZetlClipboardImage([7, 8, 9], 3, 2));

        var request = await coordinator.HandleClaimedHoldAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1),
            pending);

        AssertTrue(request is ZetlNoteCaptureRequest, "Held dual copy should open the capture dialog.");
        var capture = (ZetlNoteCaptureRequest)request!;
        AssertEqual("A1\tB1", capture.Text, "The dialog should receive the clipboard text as content.");
        AssertTrue(capture.Image is not null, "The dialog request should carry the clipboard picture.");
        coordinator.CompleteNoteCapture(
            capture,
            new ZetlNoteCaptureResult(
                Committed: true,
                NoteText: "A1\tB1 edited",
                StartProject: true,
                CreateNewProject: false,
                ProjectName: capture.Project.Name,
                SelectedBucketName: capture.PreferredBucket!.Name,
                SelectedBucket: capture.PreferredBucket));

        var note = store.GetActiveProject()!
            .Buckets.SelectMany(bucket => bucket.Slips).Single();
        AssertFalse(note.IsImage, "A held dual capture should save text-preferred.");
        AssertEqual("A1\tB1 edited", note.Text, "The edited dialog text should be the slip content.");
        AssertTrue(note.Image is not null, "The held dual capture should retain the picture.");
        AssertTrue(
            notifications.Messages.Single().StartsWith("Saved to", StringComparison.Ordinal),
            "A text-preferred dual save should report as an ordinary note.");
    }

    [Fact(DisplayName = "Runtime held dual copy with cleared text saves a picture")]
    public static async Task RuntimeHeldCopyDualClearedTextSavesPicture()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard(null, changeToken: 2),
            notifications,
            out _,
            out _);
        var pending = new ZetlPendingShortcut(
            VK_C,
            shiftLane: false,
            clipboardSequenceNumber: 1,
            captureOrigin: null);
        pending.SetObservedClipboardContent(
            "A1\tB1",
            new ZetlClipboardImage([7, 8, 9], 3, 2));

        var request = await coordinator.HandleClaimedHoldAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1),
            pending);
        var capture = (ZetlNoteCaptureRequest)request!;
        coordinator.CompleteNoteCapture(
            capture,
            new ZetlNoteCaptureResult(
                Committed: true,
                NoteText: "",
                StartProject: true,
                CreateNewProject: false,
                ProjectName: capture.Project.Name,
                SelectedBucketName: capture.PreferredBucket!.Name,
                SelectedBucket: capture.PreferredBucket));

        var note = store.GetActiveProject()!
            .Buckets.SelectMany(bucket => bucket.Slips).Single();
        AssertTrue(note.IsImage, "Clearing the dialog text should fall back to a picture slip.");
        AssertTrue(
            notifications.Messages.Single().StartsWith("Saved image to", StringComparison.Ordinal),
            "A picture fallback save should report as an image.");
    }

    [Fact(DisplayName = "Runtime downloads copied image URLs")]
    public static async Task RuntimeAutoCapturesCopiedImageUrls()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard("https://images.example/photo", changeToken: 2),
            new FakeNotificationSink(),
            out _,
            out _,
            imageUrlResolver: new FakeImageUrlResolver(
                new ZetlResolvedImageUrl(
                    new ZetlClipboardImage([4, 5, 6], 40, 30),
                    "https://cdn.example/photo.png")));

        await coordinator.OnPhysicalShortcutPassedThroughAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1));

        var note = store.GetActiveBucket()!.Slips.Single();
        AssertTrue(note.IsImage, "An image URL should become an image slip.");
        AssertEqual(
            "https://cdn.example/photo.png",
            note.Image?.SourceUrl,
            "The final downloaded image URL should remain attached to the asset descriptor.");
        AssertEqual(1, store.GetProjectAssets(project).Count, "A downloaded image URL should write one asset.");
    }

    [Fact(DisplayName = "Runtime keeps non-image URLs as text")]
    public static async Task RuntimeKeepsNonImageUrlsAsText()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Inbox"], "Inbox");
        const string url = "https://example.com/article";
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard(url, changeToken: 2),
            new FakeNotificationSink(),
            out _,
            out _,
            imageUrlResolver: new FakeImageUrlResolver(null));

        await coordinator.OnPhysicalShortcutPassedThroughAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1));

        var note = store.GetActiveBucket()!.Slips.Single();
        AssertFalse(note.IsImage, "A URL that does not resolve as an image should remain text.");
        AssertEqual(url, note.Text, "Failed image resolution must preserve the copied URL.");
    }

    [Fact(DisplayName = "Runtime held copy opens downloaded image URLs")]
    public static async Task RuntimeHeldCopyCapturesImageUrls()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard("https://images.example/photo", changeToken: 2),
            new FakeNotificationSink(),
            out _,
            out _,
            imageUrlResolver: new FakeImageUrlResolver(
                new ZetlResolvedImageUrl(
                    new ZetlClipboardImage([7, 8, 9], 3, 2),
                    "https://images.example/photo.png")));
        var context = ShortcutContext(VK_C, clipboardSequenceNumber: 1);
        var pending = new ZetlPendingShortcut(VK_C, false, 1);
        pending.SetObservedClipboardContent("https://images.example/photo", null);

        var request = await coordinator.HandleClaimedHoldAsync(context, pending)
            as ZetlNoteCaptureRequest;

        AssertTrue(request?.Image is not null, "Held copy should preview a downloaded image URL.");
        AssertEqual(
            "https://images.example/photo.png",
            request?.ImageSourceUrl,
            "Held capture should retain the downloaded image URL.");
    }

    [Fact(DisplayName = "Runtime hold cancellation prevents auto-capture")]
    public static async Task RuntimeHoldCancellationPreventsAutoCapture()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Inbox"], "Inbox");
        var clipboard = new FakeClipboard("copied text", changeToken: 2);
        var delay = new ManualDelay();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out _,
            out _,
            delay);

        var captureTask = coordinator.OnPhysicalShortcutPassedThroughAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1));
        var pending = coordinator.CancelPending(VK_C, shifted: false);
        AssertTrue(pending is not null, "Hold should find and cancel the pending copy.");
        delay.Release();
        await captureTask;

        AssertEqual(0, store.GetActiveBucket()!.Slips.Count, "Cancelled copy should not auto-capture.");
        AssertEqual("copied text", pending!.ObservedClipboardText, "Observed copy text should remain available to the hold flow.");
    }

    [Fact(DisplayName = "Runtime claimed hold prevents delayed auto-capture")]
    public static async Task RuntimeClaimedHoldPreventsDelayedAutoCapture()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Inbox"], "Inbox");
        var clipboard = new FakeClipboard("copied text", changeToken: 2);
        var delay = new ManualDelay();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out _,
            out _,
            delay);
        var context = ShortcutContext(
            VK_C,
            clipboardSequenceNumber: 1);

        var captureTask = coordinator.OnPhysicalShortcutPassedThroughAsync(
            context,
            ZetlCaptureOrigin.Create(
                "Browser",
                "browser",
                "Held copy source",
                ZetlCaptureOriginDetail.ApplicationAndWindowTitle));
        var pending = coordinator.ClaimPendingForHold(context);
        AssertTrue(
            pending is not null,
            "Hold callback should claim the pending copy immediately.");
        var holdTask = coordinator.HandleClaimedHoldAsync(
            context,
            pending);

        delay.Release();
        await Task.WhenAll(captureTask, holdTask);
        var hold = await holdTask;

        AssertEqual(
            0,
            store.GetActiveBucket()!.Slips.Count,
            "A claimed hold must not auto-save the copied text.");
        AssertTrue(
            hold is ZetlNoteCaptureRequest,
            "A claimed hold with copied text should open note capture.");
        AssertEqual(
            "copied text",
            ((ZetlNoteCaptureRequest)hold!).Text,
            "The hold request should retain the observed clipboard text.");
        AssertEqual(
            "Held copy source",
            ((ZetlNoteCaptureRequest)hold!).CaptureOrigin?.WindowTitle,
            "The hold request should retain the keydown origin while clipboard observation finishes.");
    }

    [Fact(DisplayName = "Runtime claimed copy hold resolves without polling")]
    public static async Task RuntimeClaimedCopyHoldResolvesWithoutPolling()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var delay = new ManualDelay();
        var clipboard = new FakeClipboard(
            "copied text",
            changeToken: 2);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out _,
            out _,
            delay);
        var context = ShortcutContext(
            VK_C,
            clipboardSequenceNumber: 1);
        var pending = new ZetlPendingShortcut(
            VK_C,
            shiftLane: false,
            clipboardSequenceNumber: 1);

        var copiedTask = coordinator.HandleClaimedHoldAsync(
            context,
            pending);
        AssertTrue(
            copiedTask.IsCompleted,
            "Changed clipboard text should resolve without polling.");
        AssertTrue(
            await copiedTask is ZetlNoteCaptureRequest,
            "Changed clipboard text should open note capture.");

        clipboard.SetState(null, changeToken: 2);
        var emptyContext = ShortcutContext(
            VK_C,
            clipboardSequenceNumber: 2);
        var emptyPending = new ZetlPendingShortcut(
            VK_C,
            shiftLane: false,
            clipboardSequenceNumber: 2);
        var emptyTask = coordinator.HandleClaimedHoldAsync(
            emptyContext,
            emptyPending);
        AssertTrue(
            emptyTask.IsCompleted,
            "Unchanged clipboard should resolve without polling.");
        AssertTrue(
            await emptyTask is ZetlBoardRequest,
            "Unchanged clipboard should open the Board immediately.");
    }
}
