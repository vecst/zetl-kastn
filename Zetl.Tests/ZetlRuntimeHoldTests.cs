using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Tests.ZetlTestSupport;
using Xunit;
using System;
using System.Collections.Generic;

namespace ZETL.Tests;

public class ZetlRuntimeHoldTests
{
    [Fact(DisplayName = "Runtime copy hold creates note request")]
    public static async Task RuntimeCopyHoldCreatesNoteRequest()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard(" copied ", changeToken: 2),
            new FakeNotificationSink(),
            out _,
            out _);

        var request = await coordinator.HandleHoldAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1));

        AssertTrue(request is ZetlNoteCaptureRequest, "Copied text should open note capture.");
        var note = (ZetlNoteCaptureRequest)request!;
        AssertEqual("copied", note.Text, "Copy request should contain trimmed clipboard text.");
        AssertEqual("copy", note.Source, "Copy request should preserve its source.");
        AssertFalse(note.ProjectWasActive, "First copy hold sees no active project.");
        AssertTrue(note.StartProjectDefault, "Held copy should activate the project by default.");
    }

    [Fact(DisplayName = "Runtime empty copy hold opens Board")]
    public static async Task RuntimeEmptyCopyHoldOpensBoard()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard(null, changeToken: 1),
            new FakeNotificationSink(),
            out _,
            out _);

        var request = await coordinator.HandleHoldAsync(
            ShortcutContext(VK_C, clipboardSequenceNumber: 1));

        AssertTrue(request is ZetlBoardRequest, "Copy hold without new text should open the Board.");
    }

    [Fact(DisplayName = "Runtime cut hold defaults to today's journal bucket")]
    public static async Task RuntimeCutHoldDefaultsToTodaysJournalBucket()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard(null, changeToken: 1),
            new FakeNotificationSink(),
            out _,
            out _);

        var request = await coordinator.HandleHoldAsync(
            ShortcutContext(VK_X, clipboardSequenceNumber: 1));

        AssertTrue(request is ZetlNoteCaptureRequest, "Cut hold should always open note capture.");
        var note = (ZetlNoteCaptureRequest)request!;
        AssertEqual(
            ZetlStateRules.JournalQuickNoteBucketName,
            note.PreferredBucket!.Name,
            "First quick note defaults to today's Quick Note child.");
        AssertEqual(
            ZetlStateRules.JournalBucketName(DateTime.Now, 0),
            note.Project.Buckets.Single(bucket => bucket.Id == note.PreferredBucket!.ParentBucketId).Name,
            "The Quick Note child sits under today's day parent.");
        AssertFalse(note.ProjectWasActive, "First quick note sees no active project.");
        AssertFalse(note.StartProjectDefault, "Quick note should not activate the project by default.");
    }

    [Fact(DisplayName = "Runtime select-all hold captures the selection")]
    public static async Task RuntimeSelectAllHoldCapturesTheSelection()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var clipboard = new FakeClipboard(null, changeToken: 1);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out var keyboard,
            out _);
        // Holding Ctrl+A injects a copy; simulate the app copying the selected field.
        keyboard.OnSendChord = () => clipboard.SetText("the whole field");

        var request = await coordinator.HandleHoldAsync(
            ShortcutContext(VK_A, clipboardSequenceNumber: 1));

        AssertTrue(request is ZetlNoteCaptureRequest, "Holding Ctrl+A opens a capture.");
        AssertEqual(
            "the whole field",
            ((ZetlNoteCaptureRequest)request!).Text,
            "It captures the freshly-copied selection.");
    }

    [Fact(DisplayName = "Runtime select-all shift hold captures the selection")]
    public static async Task RuntimeSelectAllShiftHoldCapturesTheSelection()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var clipboard = new FakeClipboard(null, changeToken: 1);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out var keyboard,
            out _);
        // Holding Ctrl+Shift+A injects a copy; simulate the app copying the selected field.
        keyboard.OnSendChord = () => clipboard.SetText("the whole field on shift");

        var request = await coordinator.HandleHoldAsync(
            ShortcutContext(VK_A, shifted: true, clipboardSequenceNumber: 1));

        AssertTrue(request is ZetlNoteCaptureRequest, "Holding Ctrl+Shift+A opens a capture.");
        var note = (ZetlNoteCaptureRequest)request!;
        AssertTrue(note.Shifted, "Should target the shift lane.");
        AssertEqual(
            "the whole field on shift",
            note.Text,
            "It captures the freshly-copied selection on the shift lane.");
    }

    [Fact(DisplayName = "Runtime template hold requests the picker")]
    public static async Task RuntimeTemplateHoldRequestsPicker()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard(null, changeToken: 1),
            new FakeNotificationSink(),
            out _,
            out _);

        var request = await coordinator.HandleHoldAsync(
            ShortcutContext(VK_T));

        AssertTrue(request is ZetlTemplatePickerRequest, "Held Ctrl+T should request the template picker.");
    }

    [Fact(DisplayName = "Runtime compile hold without a project opens the latest project")]
    public static async Task RuntimeCompileHoldWithoutProjectOpensLatestProject()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var older = store.CreateProject("Older", ["Inbox"], "Inbox");
        var newer = store.CreateProject("Newer", ["Inbox"], "Inbox");
        store.AddSlip(older.Buckets.First(), "old", "copy").CreatedAtUtc =
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        store.AddSlip(newer.Buckets.First(), "new", "copy").CreatedAtUtc =
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        store.ClearActiveProject();
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard(null, changeToken: 1),
            new FakeNotificationSink(),
            out _,
            out _);

        var request = await coordinator.HandleHoldAsync(
            ShortcutContext(VK_V));

        AssertEqual(
            "Newer",
            (request as ZetlCompileRequest)?.Project.Name,
            "Held Ctrl+V with no active project should compile the project last written to.");
        AssertEqual<ZetlProject?>(null, store.ActiveProject, "Opening Compile should not activate it.");
    }

    [Fact(DisplayName = "Runtime compile hold with nothing anywhere says so")]
    public static async Task RuntimeCompileHoldWithNothingAnywhereSaysSo()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard(null, changeToken: 1),
            notifications,
            out _,
            out _);

        var request = await coordinator.HandleHoldAsync(
            ShortcutContext(VK_V));

        AssertEqual<ZetlShortcutRequest?>(null, request, "There is nothing to open.");
        AssertTrue(
            notifications.Messages.Contains("No Zetl notes to compose yet."),
            "The user hears why nothing opened.");
    }

    [Fact(DisplayName = "Runtime compile hold with an active project stays compile")]
    public static async Task RuntimeCompileHoldWithActiveProjectStaysCompile()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        store.SetActiveProject(project.Id, shifted: false);
        store.AddSlip(project.Buckets.First(), "a note", "copy");
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard(null, changeToken: 1),
            new FakeNotificationSink(),
            out _,
            out _);

        var request = await coordinator.HandleHoldAsync(
            ShortcutContext(VK_V));

        AssertTrue(
            request is ZetlCompileRequest,
            "Held Ctrl+V with an active project and notes should still compile.");
    }

    [Fact(DisplayName = "Runtime hold toggles and undo stay portable")]
    public static async Task RuntimeHoldTogglesAndUndoStayPortable()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Inbox"], "Inbox");
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard(null, changeToken: 1),
            notifications,
            out _,
            out var undo);

        await coordinator.HandleHoldAsync(ShortcutContext(VK_P));
        AssertTrue(coordinator.Inner.IsPassThroughOn(false), "Ctrl+P hold should turn pass-through on for now.");
        await coordinator.HandleHoldAsync(ShortcutContext(VK_R));
        AssertEqual("Replay", store.GetActiveBucket()!.Settings.Kind, "Ctrl+R hold should enable Replay.");

        var undone = false;
        undo.Push(false, "Undone.", () => undone = true);
        await coordinator.HandleHoldAsync(ShortcutContext(VK_Z));
        AssertTrue(undone, "Ctrl+Z hold should run the latest lane undo.");
        AssertEqual("Undone.", notifications.Messages.Last(), "Undo should report its message.");
    }

    [Fact(DisplayName = "Runtime completes quick-note result")]
    public static void RuntimeCompletesQuickNoteResult()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.GetCaptureHome();
        var scratch = store.GetScratchBucket(project);
        var clipboard = new FakeClipboard("keep me", changeToken: 1);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out _,
            out _,
            quickNoteToClipboard: false);
        var request = new ZetlNoteCaptureRequest(
            Shifted: false,
            project,
            scratch,
            Text: "",
            Source: "cut",
            ProjectWasActive: false,
            StartProjectDefault: false,
            CaptureOrigin: ZetlCaptureOrigin.Create(
                "Editor",
                "editor",
                "Quick note source",
                ZetlCaptureOriginDetail.ApplicationAndWindowTitle));

        coordinator.CompleteNoteCapture(
            request,
            new ZetlNoteCaptureResult(
                Committed: true,
                NoteText: "quick note",
                StartProject: false,
                CreateNewProject: false,
                ProjectName: project.Name,
                SelectedBucketName: scratch.Name,
                SelectedBucket: scratch));

        AssertEqual("quick note", scratch.Slips.Single().Text, "Quick-note result should save the note.");
        AssertEqual(
            "Quick note source",
            scratch.Slips.Single().CaptureOrigin?.WindowTitle,
            "Completed note capture should save the origin carried by its request.");
        AssertEqual("keep me", clipboard.Text, "Quick note should preserve clipboard when disabled.");
        AssertTrue(store.GetActiveProject() is null, "Quick note should leave the project inactive.");
    }

    [Fact(DisplayName = "Runtime pastes cut back when held cut is discarded")]
    public static void RuntimePastesCutBackOnDiscardedCut()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.GetCaptureHome();
        var scratch = store.GetScratchBucket(project);
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard("cut text", changeToken: 1),
            new FakeNotificationSink(),
            out _,
            out _,
            quickNoteToClipboard: false);

        ZetlNoteCaptureRequest CutRequest(string text) => new(
            Shifted: false,
            project,
            scratch,
            Text: text,
            Source: "cut",
            ProjectWasActive: false,
            StartProjectDefault: false);

        ZetlNoteCaptureResult Cancelled() => new(
            Committed: false,
            NoteText: "",
            StartProject: false,
            CreateNewProject: false,
            ProjectName: project.Name,
            SelectedBucketName: scratch.Name,
            SelectedBucket: scratch);

        AssertEqual(
            ZetlNoteCaptureOutcome.PasteCutBack,
            coordinator.CompleteNoteCapture(CutRequest("cut text"), Cancelled()),
            "Discarding a held cut that captured text should request a paste-back.");
        AssertEqual(
            ZetlNoteCaptureOutcome.None,
            coordinator.CompleteNoteCapture(CutRequest(""), Cancelled()),
            "Discarding a held cut that captured nothing should not paste back.");
        AssertEqual(
            ZetlNoteCaptureOutcome.None,
            coordinator.CompleteNoteCapture(
                CutRequest("cut text"),
                Cancelled() with { Committed = true, NoteText = "kept" }),
            "Keeping the note should not paste the cut back.");
        AssertEqual("kept", scratch.Slips.Single(note => note.Source == "cut").Text, "Only the kept cut note should be saved; discarded cuts should not.");
    }

    [Fact(DisplayName = "Runtime files quick note into the selected project")]
    public static void RuntimeFilesQuickNoteIntoSelectedProject()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var source = store.CreateProject("Source", ["Inbox"], "Inbox");
        var target = store.CreateProject("Target", ["Notes"], "Notes");
        store.SetActiveProject(source.Id);
        var targetBucket = target.Buckets.First(bucket => bucket.Name == "Notes");
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard("", changeToken: 1),
            new FakeNotificationSink(),
            out _,
            out _,
            quickNoteToClipboard: false);

        var request = new ZetlNoteCaptureRequest(
            Shifted: false,
            source,
            store.GetQuickNoteBucket(source),
            Text: "",
            Source: "cut",
            ProjectWasActive: true,
            StartProjectDefault: false);

        coordinator.CompleteNoteCapture(
            request,
            new ZetlNoteCaptureResult(
                Committed: true,
                NoteText: "redirected jot",
                StartProject: false,
                CreateNewProject: false,
                ProjectName: source.Name,
                SelectedBucketName: targetBucket.Name,
                SelectedBucket: targetBucket,
                SelectedProject: target));

        AssertEqual("redirected jot", targetBucket.Slips.Single().Text, "Note should be filed into the selected project's bucket.");
        AssertEqual(0, source.Buckets.Sum(bucket => bucket.Slips.Count), "The request's project should receive no note.");
        AssertEqual(targetBucket.Id, target.QuickNoteBucketId, "Selected project should remember its quick-note bucket.");
        AssertEqual("Source", source.Name, "Filing into another project must not rename the request project.");
        AssertEqual(source.Id, store.GetActiveProject()?.Id, "Redirecting without activating should leave the prior active project active.");
    }

    [Fact(DisplayName = "Runtime redirects quick note with no active project")]
    public static void RuntimeRedirectsQuickNoteWithNoActiveProject()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var other = store.CreateProject("Other", ["Notes"], "Notes");
        store.ClearActiveProject();
        var dated = store.GetCaptureHome();
        var otherBucket = other.Buckets.First(bucket => bucket.Name == "Notes");
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard("", changeToken: 1),
            new FakeNotificationSink(),
            out _,
            out _,
            quickNoteToClipboard: false);

        var request = new ZetlNoteCaptureRequest(
            Shifted: false,
            dated,
            store.GetScratchBucket(dated),
            Text: "",
            Source: "cut",
            ProjectWasActive: false,
            StartProjectDefault: false);

        coordinator.CompleteNoteCapture(
            request,
            new ZetlNoteCaptureResult(
                Committed: true,
                NoteText: "redirected jot",
                StartProject: false,
                CreateNewProject: false,
                ProjectName: "Renamed Attempt",
                SelectedBucketName: otherBucket.Name,
                SelectedBucket: otherBucket,
                SelectedProject: other));

        AssertEqual("redirected jot", otherBucket.Slips.Single().Text, "Redirected jot should land in the chosen existing project.");
        AssertEqual(0, dated.Buckets.Sum(bucket => bucket.Slips.Count), "The dated default should receive no note when redirected.");
        AssertFalse(dated.Name == "Renamed Attempt", "Redirecting must not rename the dated default project.");
        AssertEqual(otherBucket.Id, other.QuickNoteBucketId, "The chosen project should remember its quick-note bucket.");
        AssertTrue(store.GetActiveProject() is null, "A redirected jot should leave no active project.");
    }

    [Fact(DisplayName = "Runtime activates the selected project from a quick note")]
    public static void RuntimeActivatesSelectedProjectFromQuickNote()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var other = store.CreateProject("Other", ["Notes"], "Notes");
        store.ClearActiveProject();
        var dated = store.GetCaptureHome();
        var otherBucket = other.Buckets.First(bucket => bucket.Name == "Notes");
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard("", changeToken: 1),
            new FakeNotificationSink(),
            out _,
            out _,
            quickNoteToClipboard: false);

        var request = new ZetlNoteCaptureRequest(
            Shifted: false,
            dated,
            store.GetScratchBucket(dated),
            Text: "",
            Source: "cut",
            ProjectWasActive: false,
            StartProjectDefault: false);

        coordinator.CompleteNoteCapture(
            request,
            new ZetlNoteCaptureResult(
                Committed: true,
                NoteText: "activate me",
                StartProject: true,
                CreateNewProject: false,
                ProjectName: other.Name,
                SelectedBucketName: otherBucket.Name,
                SelectedBucket: otherBucket,
                SelectedProject: other));

        AssertEqual("activate me", otherBucket.Slips.Single().Text, "Note should be filed into the chosen project.");
        AssertEqual(other.Id, store.GetActiveProject()?.Id, "Activating from a quick note should make the chosen project active.");
    }

    [Fact(DisplayName = "Runtime deactivates the active project when toggled off")]
    public static void RuntimeDeactivatesActiveProjectWhenToggledOff()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var active = store.CreateProject("Active", ["Inbox"], "Inbox");
        store.SetActiveProject(active.Id);
        var bucket = active.Buckets.First(item => item.Name == "Inbox");
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard("copied", changeToken: 1),
            new FakeNotificationSink(),
            out _,
            out _,
            quickNoteToClipboard: false);

        var request = new ZetlNoteCaptureRequest(
            Shifted: false,
            active,
            bucket,
            Text: "copied",
            Source: "copy",
            ProjectWasActive: true,
            StartProjectDefault: true);

        coordinator.CompleteNoteCapture(
            request,
            new ZetlNoteCaptureResult(
                Committed: true,
                NoteText: "kept",
                StartProject: false,
                CreateNewProject: false,
                ProjectName: active.Name,
                SelectedBucketName: bucket.Name,
                SelectedBucket: bucket,
                SelectedProject: active));

        AssertEqual("kept", bucket.Slips.Single().Text, "The note should still be saved when deactivating.");
        AssertTrue(store.GetActiveProject() is null, "Toggling Activate off on the active project should deactivate it.");
    }

    [Fact(DisplayName = "Runtime creates a new project from the capture dialog")]
    public static void RuntimeCreatesNewProjectFromCapture()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var origin = store.GetCaptureHome();
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard("copied", changeToken: 1),
            new FakeNotificationSink(),
            out _,
            out _,
            quickNoteToClipboard: false);

        var request = new ZetlNoteCaptureRequest(
            Shifted: false,
            origin,
            store.GetScratchBucket(origin),
            Text: "copied",
            Source: "copy",
            ProjectWasActive: true,
            StartProjectDefault: true);

        coordinator.CompleteNoteCapture(
            request,
            new ZetlNoteCaptureResult(
                Committed: true,
                NoteText: "fresh note",
                StartProject: true,
                CreateNewProject: true,
                ProjectName: "Fresh",
                SelectedBucketName: "Capture",
                SelectedBucket: null!,
                SelectedProject: null));

        var fresh = store.State.Projects.SingleOrDefault(project => project.Name == "Fresh");
        AssertTrue(fresh is not null, "Choosing New project should create the named project.");
        AssertEqual("fresh note", fresh!.Buckets.Single(bucket => bucket.Name == "Capture").Slips.Single().Text, "The note should land in the chosen bucket of the new project.");
        AssertEqual(
            "Capture|Quick Note|Scratch",
            string.Join("|", fresh.Buckets.Select(bucket => bucket.Name)),
            "A new project gets the default Capture / Quick Note pair plus its protected Scratch.");
        AssertEqual(fresh.Id, store.GetActiveProject()?.Id, "A new project created with Activate on should become active.");
    }

    [Fact(DisplayName = "Runtime completes flattened compile result")]
    public static async Task RuntimeCompletesCompileResult()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var source = store.CreateProject("Source", ["Inbox"], "Inbox");
        store.AddSlip(store.GetActiveBucket()!, "source note", "copy");
        var destination = store.CreateProject("Destination", ["Output"], "Output");
        store.SetActiveProject(source.Id);
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard(null, changeToken: 1),
            notifications,
            out _,
            out var undo);
        var request = (ZetlCompileRequest)(await coordinator.HandleHoldAsync(
            ShortcutContext(VK_V)))!;

        var outcome = coordinator.CompleteCompile(
            request,
            new ZetlCompileResult(
                Committed: true,
                CompiledText: "compiled text",
                SaveToBucket: true,
                DestinationProject: destination,
                DestinationBucketName: "Output",
                Flatten: true,
                SelectedNoteTexts: ["source note"],
                PasteNow: false));

        AssertEqual(ZetlCompileOutcome.RestoreTarget, outcome, "Saved compile should restore the target.");
        AssertEqual(
            "compiled text",
            destination.Buckets.Single(bucket => bucket.Name == "Output").Slips.Single().Text,
            "Compile result should save to the selected destination.");
        AssertTrue(undo.TryPop(false, out _), "Saved compile should be undoable.");
        AssertTrue(
            notifications.Messages.Single().StartsWith("Composed to Output in Destination."),
            "Compile should report its destination.");
    }

    [Fact(DisplayName = "Runtime preserves structured compile saves")]
    public static void RuntimePreservesStructuredCompileSaves()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var source = store.CreateProject("Source", ["Inbox"], "Inbox");
        var destination = store.CreateProject("Destination", ["Output"], "Output");
        var destinationActiveBucketId = destination.ActiveBucketId;
        store.SetActiveProject(source.Id);
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard(null, changeToken: 1),
            new FakeNotificationSink(),
            out _,
            out _);
        var request = new ZetlCompileRequest(false, source, null);

        var outcome = coordinator.CompleteCompile(
            request,
            new ZetlCompileResult(
                Committed: true,
                CompiledText: "ignored combined text",
                SaveToBucket: true,
                DestinationProject: destination,
                DestinationBucketName: "Compiled",
                Flatten: false,
                SelectedNoteTexts: ["one", "two"],
                PasteNow: false));

        var compiled = destination.Buckets.Single(bucket => bucket.Name == "Compiled");
        AssertEqual(ZetlCompileOutcome.RestoreTarget, outcome, "Structured save should restore the target.");
        AssertEqual(2, compiled.Slips.Count, "Structured save should preserve note boundaries.");
        AssertEqual("one", compiled.Slips[0].Text, "Structured save should preserve note order.");
        AssertEqual("two", compiled.Slips[1].Text, "Structured save should preserve note order.");
        AssertEqual(source.Id, store.GetActiveProject()?.Id, "Structured save should not change the active project.");
        AssertEqual(destinationActiveBucketId, destination.ActiveBucketId, "Structured save should not change the destination active bucket.");
    }

    [Fact(DisplayName = "Runtime returns copy and paste compile outcomes")]
    public static void RuntimeReturnsCopyAndPasteCompileOutcomes()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var source = store.CreateProject("Source", ["Inbox"], "Inbox");
        var clipboard = new FakeClipboard("before", changeToken: 1);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out _,
            out _);
        var request = new ZetlCompileRequest(false, source, null);

        var copyOutcome = coordinator.CompleteCompile(
            request,
            new ZetlCompileResult(
                Committed: true,
                CompiledText: "copied compile",
                SaveToBucket: false,
                DestinationProject: source,
                DestinationBucketName: "Inbox",
                Flatten: false,
                SelectedNoteTexts: ["copied compile"],
                PasteNow: false));

        AssertEqual(ZetlCompileOutcome.RestoreTarget, copyOutcome, "Copy should restore the target.");
        AssertEqual("copied compile", clipboard.Text, "Copy should place compiled text on the clipboard.");

        var pasteOutcome = coordinator.CompleteCompile(
            request,
            new ZetlCompileResult(
                Committed: true,
                CompiledText: "pasted compile",
                SaveToBucket: false,
                DestinationProject: source,
                DestinationBucketName: "Inbox",
                Flatten: false,
                SelectedNoteTexts: ["pasted compile"],
                PasteNow: true));

        AssertEqual(ZetlCompileOutcome.PasteNow, pasteOutcome, "Paste Now should request the paste path.");
        AssertEqual("pasted compile", clipboard.Text, "Paste Now should stage compiled text on the clipboard.");
    }

    [Fact(DisplayName = "Runtime formatted compile stages rich clipboard")]
    public static void RuntimeFormattedCompileStagesRichClipboard()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var source = store.CreateProject("Source", ["Inbox"], "Inbox");
        var task = store.AddSlip(source.Buckets.First(), "compiled task", "copy", blockKind: ZetlBlockKinds.Task);
        var selected = store.GetSlipDisplayItems(source)
            .Where(item => item.Slip.Id == task.Id)
            .ToList();
        var clipboard = new FakeClipboard("before", changeToken: 1);
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out _,
            out _);
        var request = new ZetlCompileRequest(false, source, null);

        var outcome = coordinator.CompleteCompile(
            request,
            new ZetlCompileResult(
                Committed: true,
                CompiledText: "Source\r\n\r\nInbox\r\n\tcompiled task",
                SaveToBucket: false,
                DestinationProject: source,
                DestinationBucketName: "Inbox",
                Flatten: false,
                SelectedNoteTexts: ["compiled task"],
                PasteNow: false,
                CompiledHtml: ZetlComposeOutput.Html(source, selected)));

        AssertEqual(ZetlCompileOutcome.RestoreTarget, outcome, "Rich copy should restore the target.");
        AssertEqual("Source\r\n\r\nInbox\r\n\tcompiled task", clipboard.Text, "Rich copy should keep the plain fallback.");
        AssertTrue(
            (clipboard.RichHtml ?? "").Contains("☐ compiled task", StringComparison.Ordinal),
            "Rich copy should use a printable task box.");
        AssertTrue(
            !(clipboard.RichHtml ?? "").Contains("<input type=\"checkbox\"", StringComparison.Ordinal),
            "Formatted clipboard output should not rely on interactive task inputs.");
        AssertTrue(
            !(clipboard.RichHtml ?? "").Contains("<body", StringComparison.OrdinalIgnoreCase)
                && !(clipboard.RichHtml ?? "").Contains("<head", StringComparison.OrdinalIgnoreCase),
            "Formatted clipboard output should be a body fragment, not a whole page.");
    }

    [Fact(DisplayName = "Runtime compile does not paste when the clipboard write fails")]
    public static void RuntimeCompileDoesNotPasteWhenClipboardWriteFails()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var source = store.CreateProject("Source", ["Inbox"], "Inbox");
        var clipboard = new FakeClipboard("before", changeToken: 1) { SetTextSucceeds = false };
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            new FakeNotificationSink(),
            out _,
            out _);
        var request = new ZetlCompileRequest(false, source, null);

        var outcome = coordinator.CompleteCompile(
            request,
            new ZetlCompileResult(
                Committed: true,
                CompiledText: "pasted compile",
                SaveToBucket: false,
                DestinationProject: source,
                DestinationBucketName: "Inbox",
                Flatten: false,
                SelectedNoteTexts: ["pasted compile"],
                PasteNow: true));

        AssertEqual(ZetlCompileOutcome.RestoreTarget, outcome, "A failed clipboard write must not request the paste path.");
        AssertEqual("before", clipboard.Text, "A failed clipboard write should leave the clipboard untouched.");
    }

    [Fact(DisplayName = "Runtime compile surfaces an uncertain clipboard rollback")]
    public static void RuntimeCompileSurfacesUncertainClipboardRollback()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var source = store.CreateProject("Source", ["Inbox"], "Inbox");
        var clipboard = new FakeClipboard("before", changeToken: 1)
        {
            WriteResultOverride = new(
                ZetlClipboardWriteStatus.WriteFailedRestoreFailed,
                FailureReason: "injected partial rollback")
        };
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            clipboard,
            notifications,
            out _,
            out _);

        var outcome = coordinator.CompleteCompile(
            new ZetlCompileRequest(false, source, null),
            new ZetlCompileResult(
                Committed: true,
                CompiledText: "uncertain compile",
                SaveToBucket: false,
                DestinationProject: source,
                DestinationBucketName: "Inbox",
                Flatten: false,
                SelectedNoteTexts: ["uncertain compile"],
                PasteNow: true));

        AssertEqual(
            ZetlCompileOutcome.RestoreTarget,
            outcome,
            "An uncertain clipboard must never request paste injection.");
        AssertTrue(
            notifications.Messages.Single().Contains(
                "could not fully restore",
                StringComparison.OrdinalIgnoreCase),
            "The user should be told that clipboard rollback was incomplete.");
    }

    [Fact(DisplayName = "Runtime reports rejected compiled paste")]
    public static async Task RuntimeReportsRejectedCompiledPaste()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(
            store,
            new FakeClipboard("compiled text", changeToken: 1),
            notifications,
            out var keyboard,
            out _);
        keyboard.PasteSucceeds = false;

        await coordinator.PasteCompiledTextAsync();

        AssertEqual(1, keyboard.PasteCount, "Compiled paste should be attempted once.");
        AssertTrue(
            notifications.Messages.Single().Contains("still on the clipboard", StringComparison.Ordinal),
            "Rejected paste should explain that the compiled text is preserved.");
        AssertTrue(
            notifications.Messages.Single().Contains("elevated", StringComparison.OrdinalIgnoreCase),
            "Rejected paste should mention the Windows privilege mismatch.");
    }
}
