using Chordl;
using static Chordl.ChordlKeys;

namespace ZETL;

internal sealed class ZetlShortcutCoordinator
{
    private readonly ZetlPendingShortcutRegistry pendingShortcuts = new();
    private readonly SemaphoreSlim[] replayLaneGates = [new(1, 1), new(1, 1)];
    private readonly SemaphoreSlim replayClipboardGate = new(1, 1);
    private readonly ZetlReplayLaneLifecycle replayLanes = new();
    private readonly ZetlStateStore store;
    private readonly IKeyboardBackend keyboard;
    private readonly IClipboard clipboard;
    private readonly ZetlReplayClipboardSession replayClipboard;
    private readonly IZetlDispatcher dispatcher;
    private readonly IZetlDelay delay;
    private readonly IZetlNotificationSink notifications;
    private readonly ZetlUndoStack undoStack;
    private readonly Func<ZetlAppSettings> getSettings;
    private readonly Action<string> log;
    private readonly IImageUrlResolver? imageUrlResolver;

    private bool autoCaptureOnCopy() => getSettings().AutoCaptureOnCopy;
    private bool quickNoteToClipboard() => getSettings().QuickNoteToClipboard;
    private bool replayResumeClipboard() => getSettings().ReplayResumeClipboard;
    private TimeSpan holdDelay => TimeSpan.FromMilliseconds(getSettings().HoldDelayMs);

    private TimeSpan ClipboardPollInterval => TimeSpan.FromMilliseconds(getSettings().ClipboardPollIntervalMs);
    private TimeSpan ClipboardObservationTimeout => TimeSpan.FromMilliseconds(getSettings().ClipboardObservationTimeoutMs);
    private TimeSpan AutoCaptureClipboardTimeout => TimeSpan.FromMilliseconds(getSettings().AutoCaptureClipboardTimeoutMs);
    private TimeSpan PopClipboardDelay => TimeSpan.FromMilliseconds(getSettings().PopClipboardDelayMs);
    private TimeSpan ReplayClipboardRestoreDelay => TimeSpan.FromMilliseconds(getSettings().ReplayClipboardRestoreDelayMs);

    public ZetlShortcutCoordinator(
        ZetlStateStore store,
        IKeyboardBackend keyboard,
        IClipboard clipboard,
        IZetlDispatcher dispatcher,
        IZetlDelay delay,
        IZetlNotificationSink notifications,
        ZetlUndoStack undoStack,
        Func<ZetlAppSettings> getSettings,
        Action<string> log,
        IImageUrlResolver? imageUrlResolver = null)
    {
        this.store = store;
        this.keyboard = keyboard;
        this.clipboard = clipboard;
        replayClipboard = new ZetlReplayClipboardSession(clipboard);
        this.dispatcher = dispatcher;
        this.delay = delay;
        this.notifications = notifications;
        this.undoStack = undoStack;
        this.getSettings = getSettings;
        this.log = log;
        this.imageUrlResolver = imageUrlResolver;
    }

    // Zetl's gesture behavior, registered under the ids the routing rules name.
    // Which key and focus lead to each action is the router's decision.
    public void RegisterActions(ZetlGestureRouter router)
    {
        router.RegisterPress(ZetlGestureActions.ObserveCopy, (context, origin) =>
            ObservePressAsync(context, origin, autoCapture: autoCaptureOnCopy()));
        router.RegisterPress(ZetlGestureActions.Observe, (context, origin) =>
            ObservePressAsync(context, origin, autoCapture: false));

        router.RegisterTap(ZetlGestureActions.PasteQueue, HandlePasteTap);

        router.RegisterHold(ZetlGestureActions.CaptureSelectAll, (context, _) =>
            CreateSelectAllCaptureRequestAsync(context));
        router.RegisterHold(ZetlGestureActions.ToggleProject, (context, _) =>
            Task.FromResult(HandleProjectToggle(context.ShiftLane)));
        router.RegisterHold(ZetlGestureActions.Board, (context, _) =>
            Task.FromResult<ZetlShortcutRequest?>(new ZetlBoardRequest(context.ShiftLane)));
        router.RegisterHold(ZetlGestureActions.CaptureCopy, CreateCopyHoldRequestAsync);
        router.RegisterHold(ZetlGestureActions.TogglePop, (context, _) =>
            Task.FromResult(HandlePopToggle(context.ShiftLane)));
        router.RegisterHold(ZetlGestureActions.ToggleReplay, (context, _) =>
            Task.FromResult(HandleReplayToggle(context.ShiftLane)));
        router.RegisterHold(ZetlGestureActions.TemplatePicker, (context, _) =>
            Task.FromResult<ZetlShortcutRequest?>(
                new ZetlTemplatePickerRequest(context.ShiftLane)));
        router.RegisterHold(ZetlGestureActions.QuickNote, (context, pending) =>
            Task.FromResult<ZetlShortcutRequest?>(CreateCutHoldRequest(context, pending)));
        router.RegisterHold(ZetlGestureActions.Compile, (context, _) =>
            Task.FromResult(CreateCompileRequest(context.ShiftLane)));
        router.RegisterHold(ZetlGestureActions.Undo, (context, _) =>
            Task.FromResult(HandleUndo(context.ShiftLane)));
    }

    // A copy or cut went through natively: remember it so a hold can use what it
    // put on the clipboard, and (for a copy) auto-capture it.
    private async Task ObservePressAsync(
        ChordlEventContext context,
        Lazy<ZetlCaptureOrigin?>? captureOrigin,
        bool autoCapture)
    {
        var pending = pendingShortcuts.Register(
            context.KeyCode,
            context.ShiftLane,
            context.ClipboardSequenceNumber,
            deferredCaptureOrigin: captureOrigin ?? new Lazy<ZetlCaptureOrigin?>((ZetlCaptureOrigin?)null));

        var observeTask = ObserveClipboardChangeAsync(pending);
        var autoCaptureTask = autoCapture
            ? AutoCaptureCopyAsync(pending)
            : Task.CompletedTask;
        await Task.WhenAll(observeTask, autoCaptureTask);
    }

    public ZetlPendingShortcut? CancelPending(int keyCode, bool shifted)
    {
        return pendingShortcuts.Claim(keyCode, shifted);
    }

    public ZetlPendingShortcut? ClaimPendingForHold(
        ChordlEventContext context)
    {
        return CancelPending(context.KeyCode, context.ShiftLane);
    }

    // A tapped paste: Replay pastes the next queued item instead, Pop lets the
    // paste through and removes the matching item. Both paste chords share it;
    // the lane comes from ShiftLane, not from ReplayShift, which only describes
    // the pass-through replay chord.
    private bool HandlePasteTap(ChordlEventContext context)
    {
        var shiftLane = context.ShiftLane;
        if (replayLanes.IsRestoring(shiftLane))
        {
            // The final Replay item is still on the clipboard. Suppress this
            // physical paste until conditional restoration and finalization
            // settle instead of passing the staged item through a second time.
            return true;
        }

        var activeBucket = store.GetActiveBucket(shiftLane);
        if (activeBucket is not null && ZetlStateStore.IsReplayBucket(activeBucket))
        {
            // The hook callback needs the handled decision synchronously, but the
            // replay clipboard read/write and synthetic paste must not run on the
            // low-level keyboard hook thread: blocking it stalls system-wide input
            // and can make Windows silently remove the hook. Enqueue that work
            // onto the dispatcher and return the decision immediately.
            dispatcher.Post(() => ZetlAsync.RunLogged(
                () => HandleReplayTapAsync(shiftLane, activeBucket), "replay tap", log));
            return true;
        }

        ZetlAsync.RunLogged(() => HandlePopTapAsync(context.ShiftLane), "pop tap", log);
        return false;
    }

    public ZetlNoteCaptureOutcome CompleteNoteCapture(
        ZetlNoteCaptureRequest request,
        ZetlNoteCaptureResult result)
    {
        if (!result.Committed
            || (request.Image is null && string.IsNullOrWhiteSpace(result.NoteText)))
        {
            // Esc (or an empty save) changes nothing: the lane keeps whatever
            // project was active before the gesture.
            // A held Ctrl+X already performed the physical cut before the dialog
            // opened, so discarding the note leaves the source missing its text.
            // Signal the host to paste the still-on-clipboard cut text back.
            return WasHeldCut(request)
                ? ZetlNoteCaptureOutcome.PasteCutBack
                : ZetlNoteCaptureOutcome.None;
        }

        // Default to the project the dialog chose (the quick-note project
        // selector), falling back to the request's project when none was set.
        // Held cut and held copy share one dialog and one save path. The only
        // differences are operation-inherent: a cut keeps the clipboard per the
        // quick-note setting and remembers its bucket; a copy always syncs the
        // clipboard with the saved note.
        var isCut = string.Equals(request.Source, "cut", StringComparison.OrdinalIgnoreCase);

        // The note files into the project chosen in the dialog's selector.
        var noteProject = result.SelectedProject ?? request.Project;
        if (result.CreateNewProject)
        {
            var bucketNames = store.Defaults.ResolvedProjectBuckets;
            noteProject = store.CreateProject(
                result.ProjectName,
                bucketNames,
                result.SelectedBucketName,
                request.Shifted);
        }

        var bucket = result.CreateNewProject
            ? noteProject.Buckets.FirstOrDefault(item =>
                string.Equals(
                    item.Name,
                    result.SelectedBucketName,
                    StringComparison.OrdinalIgnoreCase))
                ?? noteProject.Buckets.First()
            : result.SelectedBucket;

        if (isCut)
        {
            store.SetQuickNoteBucket(noteProject, bucket.Id);
        }

        store.RecordCaptureActivity(noteProject);

        // A request carrying both clipboard text and a picture is a dual
        // capture: the (possibly edited) text is the content and the slip
        // saves text-preferred. Clearing the text in the dialog falls back to
        // an ordinary picture slip via AddImageSlip's blank-text guard.
        var note = request.Image is not null
            ? store.AddImageSlip(
                noteProject,
                bucket,
                request.Image,
                request.Source,
                request.CaptureOrigin,
                result.NoteText,
                request.ImageSourceUrl,
                preferTextContent: !string.IsNullOrWhiteSpace(request.Text),
                richHtml: RichHtmlForSavedText(request, result.NoteText),
                replayFormats: ReplayFormatsForSavedText(request, result.NoteText))
            : store.AddSlip(
                bucket,
                result.NoteText,
                request.Source,
                captureOrigin: request.CaptureOrigin,
                richHtml: RichHtmlForSavedText(request, result.NoteText),
                replayFormats: ReplayFormatsForSavedText(request, result.NoteText));
        undoStack.Push(
            request.Shifted,
            $"Undid save to {bucket.Name}.",
            () => store.DeleteSlip(bucket, note.Id));
        var savedRichHtml = RichHtmlForSavedText(request, result.NoteText);
        var savedReplayFormats = ReplayFormatsForSavedText(request, result.NoteText);
        var clipboardWrite = request.Image is null
            && (!isCut || quickNoteToClipboard())
                ? ZetlClipboardContentWriter.Write(
                    clipboard,
                    result.NoteText,
                    savedRichHtml,
                    savedReplayFormats)
                : null;
        if (clipboardWrite is { Succeeded: false })
        {
            // The note is already saved; don't roll it back, just record that the
            // clipboard didn't pick up the saved text.
            log(clipboardWrite.ClipboardPreserved
                ? $"Saved note to {bucket.Name}, but copying it to the clipboard failed without changing the clipboard."
                : $"Saved note to {bucket.Name}, but copying it failed and the original clipboard could not be restored completely.");
        }

        // The Activate toggle decides the lane's active project. When off, the
        // chosen project is deactivated if it is the one currently active;
        // otherwise the prior active project (or none) is left untouched.
        if (result.StartProject)
        {
            store.SetActiveProject(noteProject.Id, request.Shifted);
        }
        else if (store.GetActiveProject(request.Shifted)?.Id == noteProject.Id)
        {
            store.ClearActiveProject(request.Shifted);
        }

        notifications.Show(note.IsImage
            ? $"Saved image to {ZetlRuntimeLabels.Destination(noteProject, bucket)}."
            : $"Saved to {ZetlRuntimeLabels.Destination(noteProject, bucket)}.");
        return ZetlNoteCaptureOutcome.None;
    }

    private static string? RichHtmlForSavedText(
        ZetlNoteCaptureRequest request,
        string savedText) =>
        string.Equals(request.Text.Trim(), savedText.Trim(), StringComparison.Ordinal)
            ? request.RichHtml
            : null;

    private static IReadOnlyList<ZetlClipboardFormatData>? ReplayFormatsForSavedText(
        ZetlNoteCaptureRequest request,
        string savedText) =>
        string.Equals(request.Text.Trim(), savedText.Trim(), StringComparison.Ordinal)
            ? request.ReplayFormats
            : null;

    // True when this capture came from a held cut that actually removed text
    // (Ctrl+X / Ctrl+Shift+X with a non-empty selection). Held Ctrl+C copies are
    // excluded: a copy leaves the source intact, so there is nothing to restore.
    private static bool WasHeldCut(ZetlNoteCaptureRequest request)
    {
        return string.Equals(request.Source, "cut", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(request.Text);
    }

    public ZetlCompileOutcome CompleteCompile(
        ZetlCompileRequest request,
        ZetlCompileResult result)
    {
        if (!result.Committed || string.IsNullOrWhiteSpace(result.CompiledText))
        {
            return ZetlCompileOutcome.None;
        }

        if (result.SaveToBucket)
        {
            var destination = store.GetOrCreateBucket(
                result.DestinationProject,
                result.DestinationBucketName,
                setActive: false);
            var destinationLabel = ZetlRuntimeLabels.Destination(
                result.DestinationProject,
                destination);
            string savedSummary;
            if (result.Flatten)
            {
                var note = store.AddSlip(destination, result.CompiledText, "compile");
                undoStack.Push(
                    request.Shifted,
                    $"Undid compose into {destination.Name}.",
                    () => store.DeleteSlip(destination, note.Id));
                savedSummary = $"to {destinationLabel}";
            }
            else
            {
                var notes = store.AddSlips(destination, result.SelectedNoteTexts, "compile");
                undoStack.Push(
                    request.Shifted,
                    $"Undid compose into {destination.Name}.",
                    () =>
                    {
                        foreach (var note in notes)
                        {
                            store.DeleteSlip(destination, note.Id);
                        }
                    });
                savedSummary = $"{notes.Count} notes to {destinationLabel}";
            }

            notifications.Show($"Composed {savedSummary}.");
            return ZetlCompileOutcome.RestoreTarget;
        }

        var clipboardWrite = ZetlClipboardContentWriter.Write(
            clipboard,
            result.CompiledText,
            string.IsNullOrWhiteSpace(result.CompiledHtml) ? null : result.CompiledHtml,
            replayFormats: null);
        if (!clipboardWrite.Succeeded)
        {
            // Staging failed, so don't paste stale clipboard content or claim a
            // copy succeeded. The compiled text can be re-produced by compiling
            // again.
            notifications.Show(clipboardWrite.ClipboardPreserved
                ? "Couldn't copy the composed text; your clipboard was left unchanged."
                : "Couldn't copy the composed text, and Zetl could not fully restore your previous clipboard.");
            return ZetlCompileOutcome.RestoreTarget;
        }

        if (result.PasteNow)
        {
            return ZetlCompileOutcome.PasteNow;
        }

        notifications.Show("Copied composed text to clipboard.");
        return ZetlCompileOutcome.RestoreTarget;
    }

    public async Task PasteCompiledTextAsync()
    {
        await delay.WaitAsync(PopClipboardDelay);
        var pasted = await keyboard.SendPaste();
        dispatcher.Post(() => notifications.Show(pasted
            ? "Pasted composed text."
            : "Paste failed; the composed text is still on the clipboard. If the target is elevated, run Zetl elevated too."));
    }

    // Re-paste the cut text into the restored foreground target after a held
    // Ctrl+X note was discarded, undoing the physical cut.
    public async Task PasteCutBackAsync()
    {
        await delay.WaitAsync(PopClipboardDelay);
        var pasted = await keyboard.SendPaste();
        dispatcher.Post(() => notifications.Show(pasted
            ? "Restored the cut text."
            : "Couldn't restore the cut text; it remains on the clipboard."));
    }

    public void ResetReplayClipboardTracking(bool shifted)
    {
        replayClipboard.Reset(shifted);
    }

    private void RestoreOriginalClipboard(bool shifted)
    {
        var outcome = replayClipboard.RestoreOriginalIfOwned(shifted);
        if (outcome is ZetlClipboardRestoreOutcome.Failed
            or ZetlClipboardRestoreOutcome.FailedClipboardUncertain)
        {
            log("Replay stopped, but restoring your previous clipboard failed.");
            notifications.Show(outcome == ZetlClipboardRestoreOutcome.FailedClipboardUncertain
                ? "Replay stopped, but Zetl could not fully restore your previous clipboard."
                : "Replay stopped without replacing your current clipboard.");
        }
    }

    // Held Ctrl+A: the physical select-all already passed through (dispatch None), so
    // the field is selected. Select-all leaves nothing on the clipboard, so — unlike
    // copy-hold, where the user's own Ctrl+C already copied — Zetl copies the selection
    // itself (same injection path as paste/replay), waits for the clipboard to reflect
    // it, then captures it exactly like a copy-hold. One quick Ctrl+A,Ctrl+C in a hold.
    private async Task<ZetlShortcutRequest?> CreateSelectAllCaptureRequestAsync(ChordlEventContext context)
    {
        if (!await keyboard.SendChord(VK_C, includeShift: false, restoreCtrl: false, restoreShift: false))
        {
            return new ZetlBoardRequest(context.ShiftLane);
        }

        await WaitForClipboardChangeAsync(context.ClipboardSequenceNumber, ClipboardObservationTimeout);
        return await CreateCopyHoldRequestAsync(context, pending: null);
    }

    private async Task WaitForClipboardChangeAsync(uint beforeSequence, TimeSpan timeout)
    {
        var elapsed = TimeSpan.Zero;
        while (elapsed < timeout && clipboard.GetChangeToken() == beforeSequence)
        {
            await delay.WaitAsync(ClipboardPollInterval);
            elapsed += ClipboardPollInterval;
        }
    }

    private async Task<ZetlShortcutRequest?> CreateCopyHoldRequestAsync(
        ChordlEventContext context,
        ZetlPendingShortcut? pending)
    {
        var project = store.GetCaptureHome(context.ShiftLane);
        var projectWasActive = store.GetActiveProject(context.ShiftLane)?.Id == project.Id;
        var clipboardContent = ResolveHoldClipboardContent(context, pending);
        if (clipboardContent is { Private: true })
        {
            notifications.Show("That copy was marked private by the app it came from, so Zetl didn't read it.");
            return null;
        }

        var pendingImage = clipboardContent?.Image;
        if (pendingImage is { } image)
        {
            // A dual clipboard (spreadsheet cells) carries its text into the
            // dialog as the editable content; the picture rides along and the
            // save files text-preferred. An image-only clipboard keeps the
            // picture-and-caption dialog.
            var imageBucket = store.ResolveCaptureBucket(project, context.ShiftLane);
            return new ZetlNoteCaptureRequest(
                context.ShiftLane,
                project,
                imageBucket,
                clipboardContent?.Text ?? "",
                "copy",
                ProjectWasActive: projectWasActive,
                StartProjectDefault: true,
                CaptureOrigin: pending?.CaptureOrigin,
                Image: image,
                RichHtml: clipboardContent?.Html,
                ReplayFormats: clipboardContent?.ReplayFormats);
        }

        var text = clipboardContent?.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ZetlBoardRequest(context.ShiftLane);
        }

        var resolvedUrl = await TryResolveImageUrlAsync(text);
        if (resolvedUrl is not null)
        {
            var imageBucket = store.ResolveCaptureBucket(project, context.ShiftLane);
            return new ZetlNoteCaptureRequest(
                context.ShiftLane,
                project,
                imageBucket,
                "",
                "copy",
                ProjectWasActive: projectWasActive,
                StartProjectDefault: true,
                CaptureOrigin: pending?.CaptureOrigin,
                Image: resolvedUrl.Image,
                ImageSourceUrl: resolvedUrl.SourceUrl);
        }

        // Held copy capture shares the quick-note dialog: same project selector,
        // Activate toggle, and bucket access. It differs by preferring the active
        // bucket as the default destination, activating the project by default
        // (StartProjectDefault), and keeping the clipboard in sync with the saved
        // note (handled in CompleteNoteCapture).
        var preferredBucket = store.ResolveCaptureBucket(project, context.ShiftLane);
        return new ZetlNoteCaptureRequest(
            context.ShiftLane,
            project,
            preferredBucket,
            text,
            "copy",
            ProjectWasActive: projectWasActive,
            StartProjectDefault: true,
            CaptureOrigin: pending?.CaptureOrigin,
            RichHtml: clipboardContent?.Html,
            ReplayFormats: clipboardContent?.ReplayFormats);
    }

    private ZetlShortcutRequest CreateCutHoldRequest(
        ChordlEventContext context,
        ZetlPendingShortcut? pending)
    {
        var project = store.GetCaptureHome(context.ShiftLane);
        var projectWasActive = store.GetActiveProject(context.ShiftLane)?.Id == project.Id;
        // A journal takes quick notes in today's Quick Note child; a project in the
        // bucket its last quick note went to.
        var preferredBucket = project.JournalMode
            ? store.GetScratchBucket(project)
            : store.GetQuickNoteBucket(project);
        var clipboardContent = ResolveHoldClipboardContent(context, pending);
        var text = clipboardContent?.Text ?? "";
        return new ZetlNoteCaptureRequest(
            context.ShiftLane,
            project,
            preferredBucket,
            text,
            "cut",
            ProjectWasActive: projectWasActive,
            StartProjectDefault: false,
            CaptureOrigin: pending?.CaptureOrigin,
            RichHtml: clipboardContent?.Html,
            ReplayFormats: clipboardContent?.ReplayFormats);
    }

    // Held copy/cut uses one already-observed generation or one fresh coherent
    // fallback capture. It must not independently re-read companion formats.
    private ZetlClipboardCaptureSnapshot? ResolveHoldClipboardContent(
        ChordlEventContext context,
        ZetlPendingShortcut? pending)
    {
        return pending?.GetObservedClipboardContent()
            ?? TryCaptureChangedClipboardContent(
                pending?.ClipboardSequenceNumber
                    ?? context.ClipboardSequenceNumber);
    }

    private ZetlShortcutRequest? CreateCompileRequest(bool shifted)
    {
        // With no active project, Compile opens on the project last written to.
        // Its source picker lists the other recent projects first, and opening
        // it activates nothing.
        var project = store.GetActiveProject(shifted)
            ?? store.GetMostRecentCompilableProject();
        if (project is null || !store.HasCompilableSlips(project))
        {
            notifications.Show("No Zetl notes to compose yet.");
            return null;
        }

        return new ZetlCompileRequest(shifted, project, BucketScope: null);
    }

    private ZetlShortcutRequest? HandleProjectToggle(bool shifted)
    {
        var (outcome, name) = store.ToggleActiveProject(shifted);
        notifications.Show(outcome switch
        {
            ZetlProjectToggleOutcome.Activated => $"Activated {name}.",
            ZetlProjectToggleOutcome.ReturnedToJournal => $"Back to {name}.",
            _ => "No recent project to activate."
        });
        return null;
    }

    private ZetlShortcutRequest? HandlePopToggle(bool shifted)
    {
        var bucket = store.GetActiveBucket(shifted);
        if (bucket is null)
        {
            notifications.Show("No active bucket yet.");
            return null;
        }

        if (ZetlStateStore.IsReplayBucket(bucket))
        {
            store.SetBucketKind(bucket, "Standard");
            store.SetBucketPopMode(bucket, true);
            if (replayResumeClipboard())
            {
                RestoreOriginalClipboard(shifted);
            }
            ResetReplayClipboardTracking(shifted);
            notifications.Show($"{bucket.Name} pop is on.");
            return null;
        }

        store.ToggleActiveBucketPopMode(shifted);
        notifications.Show($"{bucket.Name} pop is {(bucket.Settings.PopMode ? "on" : "off")}.");
        return null;
    }

    private ZetlShortcutRequest? HandleReplayToggle(bool shifted)
    {
        var bucket = store.GetActiveBucket(shifted);
        if (bucket is null)
        {
            notifications.Show("No active bucket yet.");
            return null;
        }

        if (ZetlStateStore.IsReplayBucket(bucket))
        {
            store.SetBucketKind(bucket, "Standard");
            if (replayResumeClipboard())
            {
                RestoreOriginalClipboard(shifted);
            }
            ResetReplayClipboardTracking(shifted);
            notifications.Show($"{bucket.Name} replay is off.");
            return null;
        }

        store.SetBucketKind(bucket, "Replay");
        ResetReplayClipboardTracking(shifted);
        notifications.Show($"{bucket.Name} replay is on.");
        return null;
    }

    private ZetlShortcutRequest? HandleUndo(bool shifted)
    {
        if (!undoStack.TryPop(shifted, out var action) || action is null)
        {
            notifications.Show("Nothing to undo.");
            return null;
        }

        try
        {
            action.Undo();
            notifications.Show(action.Message);
        }
        catch (Exception ex)
        {
            log($"Undo failed: {ex.Message}");
            notifications.Show("Zetl undo failed.");
        }

        return null;
    }

    private async Task ObserveClipboardChangeAsync(ZetlPendingShortcut pending)
    {
        await ObserveClipboardContentAsync(pending, ClipboardObservationTimeout);
    }

    private ZetlClipboardCaptureSnapshot? TryCaptureChangedClipboardContent(
        uint beforeSequence)
    {
        try
        {
            var content = clipboard.TryCaptureContent();
            if (content is null || content.ChangeToken == beforeSequence)
            {
                return null;
            }

            if (content.Private)
            {
                return content;
            }

            var text = string.IsNullOrWhiteSpace(content.Text)
                ? null
                : content.Text.Trim();
            var html = string.IsNullOrWhiteSpace(content.Html)
                ? null
                : content.Html;
            return content with
            {
                Text = text,
                Html = html,
                ReplayFormats = text is null ? null : content.ReplayFormats
            };
        }
        catch (Exception ex)
        {
            log($"Clipboard content read failed: {ex.Message}");
            return null;
        }
    }

    private async Task ObserveClipboardContentAsync(
        ZetlPendingShortcut pending,
        TimeSpan timeout)
    {
        var elapsed = TimeSpan.Zero;
        do
        {
            var content = TryCaptureChangedClipboardContent(
                pending.ClipboardSequenceNumber);
            if (content is not null)
            {
                // Spreadsheets can carry text, HTML, a native Replay bundle,
                // and a bitmap rendering. The backend returns all of them from
                // the same generation or no snapshot at all. A private copy is
                // final too: there is nothing to wait for.
                if (content.Private || content.Image is not null || content.Text is not null)
                {
                    pending.SetObservedClipboardContent(content);
                    return;
                }
            }

            await delay.WaitAsync(ClipboardPollInterval);
            elapsed += ClipboardPollInterval;
        }
        while (elapsed < timeout);
    }

    private async Task AutoCaptureCopyAsync(ZetlPendingShortcut pending)
    {
        await delay.WaitAsync(holdDelay + TimeSpan.FromMilliseconds(25));
        if (pending.Cancelled)
        {
            return;
        }

        var observed = pending.GetObservedClipboardContent();
        if (observed is { Private: true })
        {
            // Marked private by its app (a password manager): never captured.
            return;
        }

        if (observed is null)
        {
            await ObserveClipboardContentAsync(pending, AutoCaptureClipboardTimeout);
            observed = pending.GetObservedClipboardContent();
        }

        var text = observed?.Text;
        var image = observed?.Image;
        var richHtml = observed?.Html;
        var replayFormats = observed?.ReplayFormats;
        if ((text is null && image is null) || pending.Cancelled)
        {
            return;
        }

        string? imageSourceUrl = null;
        if (image is null && text is not null)
        {
            var resolvedUrl = await TryResolveImageUrlAsync(text);
            if (resolvedUrl is not null)
            {
                image = resolvedUrl.Image;
                imageSourceUrl = resolvedUrl.SourceUrl;
                text = null;
            }
        }

        if (pending.Cancelled)
        {
            return;
        }

        dispatcher.Post(() =>
        {
            if (pending.Cancelled)
            {
                return;
            }

            // The active project, or the Journal when the idle-copy setting
            // captures there; with neither, copy behaves like a plain copy.
            var project = store.GetTapCaptureProject(pending.ShiftLane);
            if (project is null)
            {
                return;
            }

            // Journals roll to today's Capture child (also closing a stale-day gap where
            // this path read a persisted, possibly outdated active bucket). Non-journal
            // projects keep the exact active-bucket-or-nothing behavior.
            var bucket = project.JournalMode
                ? store.RollJournalBucket(project, DateTime.Now)
                : store.GetActiveBucket(pending.ShiftLane);
            if (bucket is null)
            {
                return;
            }

            store.RecordCaptureActivity(project);

            // Both formats present (e.g. spreadsheet cells): keep both on one
            // slip, presenting as text. The picture rides along so Kastn can
            // offer the alternate representation later.
            var note = image is not null
                ? store.AddImageSlip(
                    project,
                    bucket,
                    image,
                    "copy",
                    pending.CaptureOrigin,
                    caption: text,
                    sourceUrl: imageSourceUrl,
                    preferTextContent: text is not null,
                    richHtml: text is not null ? richHtml : null,
                    replayFormats: text is not null ? replayFormats : null)
                : store.AddSlip(
                    bucket,
                    text!,
                    "copy",
                    captureOrigin: pending.CaptureOrigin,
                    richHtml: richHtml,
                    replayFormats: replayFormats);
            var capturedAsImage = image is not null && text is null;
            undoStack.Push(
                pending.ShiftLane,
                capturedAsImage
                    ? $"Undid image capture to {bucket.Name}."
                    : $"Undid capture to {bucket.Name}.",
                () => store.DeleteSlip(bucket, note.Id));
            notifications.Show(capturedAsImage
                ? $"Captured image to {ZetlRuntimeLabels.Destination(project, bucket)}."
                : $"Captured to {ZetlRuntimeLabels.Destination(project, bucket)}.");
        });
    }

    private async Task<ZetlResolvedImageUrl?> TryResolveImageUrlAsync(string text)
    {
        if (imageUrlResolver is null)
        {
            return null;
        }

        try
        {
            return await imageUrlResolver.TryResolveAsync(text);
        }
        catch (Exception ex)
        {
            log($"Image URL resolver failed: {ex.Message}");
            return null;
        }
    }

    // Runs on the dispatcher thread (enqueued from OnTapDispatched), never on the
    // low-level keyboard hook thread, so the clipboard reads/writes and paste here
    // cannot stall system input. Awaits the *real* synthetic-paste result before
    // consuming the note, so a paste Windows never accepted (e.g. blocked by an
    // elevated target) keeps the item instead of dropping it. Running on one
    // thread also keeps the replay-clipboard tracking arrays free of the
    // hook-vs-dispatcher race the inline version had.
    private async Task HandleReplayTapAsync(bool shifted, ZetlBucket activeBucket)
    {
        var laneGate = replayLaneGates[ZetlLanes.Index(shifted)];
        await laneGate.WaitAsync();
        try
        {
            // A second tap can be queued while the first still owns this lane.
            // Revalidate after entering the gate so it cannot replay an item or
            // run the empty-queue pass-through after the first tap finalized.
            if (replayLanes.IsRestoring(shifted)
                || !ReferenceEquals(store.GetActiveBucket(shifted), activeBucket)
                || !ZetlStateStore.IsReplayBucket(activeBucket))
            {
                return;
            }

            await HandleReplayTapCoreAsync(shifted, activeBucket);
        }
        finally
        {
            laneGate.Release();
        }
    }

    private async Task HandleReplayTapCoreAsync(bool shifted, ZetlBucket activeBucket)
    {
        if (!store.TryPeekNextReplaySlip(activeBucket, out var replayNote) || replayNote is null)
        {
            // The bucket is empty, so replay is genuinely done -- return to
            // Standard regardless. But this tap suppressed the physical Ctrl+V, so
            // send the user's own clipboard through as the final pass-through and
            // report if even that paste didn't land (e.g. an elevated target).
            var finish = store.FinishReplayBucket(activeBucket, shifted);

            await replayClipboardGate.WaitAsync();
            bool finalPasted;
            try
            {
                finalPasted = await keyboard.SendPaste();
            }
            finally
            {
                replayClipboardGate.Release();
            }
            dispatcher.Post(() => notifications.Show((finalPasted
                ? $"{finish.Name} replay complete."
                : $"{finish.Name} replay complete, but the final paste didn't land.")
                + ReturnedToSuffix(finish)));
            return;
        }

        var project = store.GetActiveProject(shifted);
        var noteId = replayNote.Id;
        var replayItem = replayNote.IsImage
            ? project is not null && store.ReadImageAsset(project, replayNote) is { } bytes
                ? ZetlClipboardSnapshot.FromImage(new ZetlClipboardImage(
                    bytes,
                    replayNote.Image!.Width,
                    replayNote.Image.Height))
                : null
            : ZetlClipboardSnapshot.FromText(
                replayNote.Text,
                replayNote.RichHtml,
                replayNote.ReplayFormats);
        var bucketName = activeBucket.Name;
        if (replayItem is null)
        {
            notifications.Show($"Paste failed; {bucketName} image asset is unavailable.");
            return;
        }

        var resumeClipboard = replayResumeClipboard();
        string? backupWarning = null;
        await replayClipboardGate.WaitAsync();
        bool clipboardStaged = false;
        bool pasted = false;
        uint injectedToken = 0;
        try
        {
            // A lane waiting for the shared clipboard may resume off the UI
            // thread. Marshal staging back to the dispatcher before injection.
            await RunOnDispatcherAsync(() =>
            {
                // A clipboard Zetl cannot back up must not block Replay: the
                // physical paste is already suppressed, so refusing here would
                // leave paste dead until the user copied something else.
                if (resumeClipboard
                    && replayClipboard.PreserveUserClipboard(shifted, out var backupFailure)
                        == ZetlReplayBackupOutcome.Unavailable)
                {
                    log($"Replay could not back up the clipboard ({backupFailure}); pasting without restoring it.");
                    backupWarning = "your previous clipboard can't be restored afterwards";
                }
                var stageResult = replayClipboard.Stage(
                    shifted,
                    replayItem,
                    out injectedToken);
                clipboardStaged = stageResult.Succeeded;
                if (!clipboardStaged)
                {
                    notifications.Show(stageResult.ClipboardPreserved
                        ? $"Paste paused; {bucketName} item kept and your clipboard was preserved."
                        : $"Paste paused; {bucketName} item kept, but Zetl could not fully restore your previous clipboard.");
                }
            });
            if (clipboardStaged)
            {
                // Await the actual injection result, not just that the paste was
                // queued. The shared gate keeps another lane from replacing the
                // staged clipboard before Windows accepts this chord.
                pasted = await keyboard.SendPaste();
            }
        }
        finally
        {
            replayClipboardGate.Release();
        }
        if (!clipboardStaged)
        {
            return;
        }

        // The await may resume off the dispatcher thread, so marshal the store
        // mutations back through the dispatcher. Await that posted mutation too:
        // the per-lane gate must remain held until this exact item is consumed,
        // otherwise a rapid second tap can peek and paste the same slip again.
        var pasteResult = await RunOnDispatcherAsync(() =>
        {
            if (!pasted)
            {
                notifications.Show($"Paste failed; {bucketName} item kept.");
                return new ReplayPasteResult(Pasted: false, ReplayComplete: false);
            }

            ZetlBucket? reviewBucket = null;
            ZetlSlip? consumedSlip = null;
            ZetlSlip? reviewSlip = null;
            var consumed = project is not null
                ? store.TryConsumeReplaySlipToReview(
                    project,
                    activeBucket,
                    noteId,
                    out reviewBucket,
                    out consumedSlip,
                    out reviewSlip)
                : store.TryConsumeReplaySlip(activeBucket, noteId, out consumedSlip);
            if (consumed && reviewBucket is not null)
            {
                log($"Archived replay paste from {bucketName} to {reviewBucket.Name}.");
            }

            if (!consumed)
            {
                notifications.Show($"Paste landed, but {bucketName} changed before Zetl could consume its item.");
                return new ReplayPasteResult(Pasted: true, ReplayComplete: false);
            }

            var replayComplete = !store.TryPeekNextReplaySlip(activeBucket, out _);
            // Finishing a consumable hands the lane back, so its last item
            // isn't undoable into a project that is no longer active.
            var finishingConsumable = replayComplete
                && project is not null
                && ZetlStateStore.IsConsumableProject(project);
            if (consumed && consumedSlip is not null && !finishingConsumable)
            {
                var undoReviewBucket = reviewBucket;
                var undoReviewSlipId = reviewSlip?.Id;
                undoStack.Push(
                    shifted,
                    $"Restored replay item to {bucketName}.",
                    () => store.RestoreReplayConsumedSlip(
                        activeBucket,
                        consumedSlip,
                        undoReviewBucket,
                        undoReviewSlipId));
            }

            if (replayComplete && !replayLanes.TryBeginRestoring(shifted))
            {
                throw new InvalidOperationException("Replay lane entered final restoration twice.");
            }

            if (!replayComplete)
            {
                notifications.Show(backupWarning is null
                    ? $"Pasted next item from {bucketName}."
                    : $"Pasted next item from {bucketName}; {backupWarning}.");
            }

            return new ReplayPasteResult(Pasted: true, replayComplete);
        });

        if (!pasteResult.Pasted)
        {
            return;
        }

        if (!pasteResult.ReplayComplete)
        {
            if (resumeClipboard)
            {
                ZetlAsync.RunLogged(
                    () => RestoreUserClipboardAfterReplayAndReportAsync(
                        shifted,
                        replayItem,
                        injectedToken),
                    "replay clipboard restore",
                    log);
            }
            return;
        }

        ZetlClipboardRestoreOutcome? restoreOutcome = null;
        try
        {
            if (resumeClipboard)
            {
                restoreOutcome = await RestoreUserClipboardAfterReplayAsync(
                    shifted,
                    replayItem,
                    injectedToken);
            }
        }
        catch (Exception ex)
        {
            log($"Replay finished but clipboard restoration threw: {ex.Message}");
            restoreOutcome = ZetlClipboardRestoreOutcome.FailedClipboardUncertain;
        }

        await RunOnDispatcherAsync(() =>
        {
            try
            {
                var finish = store.FinishReplayBucket(activeBucket, shifted);
                ShowReplayCompletion(bucketName, restoreOutcome, ReturnedToSuffix(finish));
            }
            finally
            {
                replayLanes.CompleteRestoring(shifted);
            }
        });
    }

    private readonly record struct ReplayPasteResult(
        bool Pasted,
        bool ReplayComplete);

    private Task RunOnDispatcherAsync(Action action)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    private Task<T> RunOnDispatcherAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(() =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    private async Task HandlePopTapAsync(bool shifted)
    {
        await delay.WaitAsync(PopClipboardDelay);
        var content = clipboard.TryCaptureContent();
        if (content is null || (content.Text is null && content.Image is null))
        {
            return;
        }

        dispatcher.Post(() =>
        {
            // A paste can carry both formats (spreadsheet cells). Match the
            // image hash first, then fall back to the text so a dual paste can
            // still pop a text-only slip.
            ZetlBucket? bucket = null;
            ZetlSlip? note = null;
            ZetlBucket? reviewBucket = null;
            ZetlSlip? reviewNote = null;
            var popped = content.Image is not null
                && store.TryPopLastMatchingActiveImage(
                    Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(content.Image.PngBytes))
                        .ToLowerInvariant(),
                    shifted,
                    out bucket,
                    out note,
                    out reviewBucket,
                    out reviewNote);
            if (!popped && content.Text is not null)
            {
                popped = store.TryPopLastMatchingActiveSlip(
                    content.Text,
                    shifted,
                    out bucket,
                    out note,
                    out reviewBucket,
                    out reviewNote);
            }

            if (popped
                && bucket is not null
                && note is not null
                && reviewBucket is not null
                && reviewNote is not null)
            {
                undoStack.Push(
                    shifted,
                    $"Restored popped item to {bucket.Name} from {reviewBucket.Name}.",
                    () => store.RestorePoppedSlip(bucket, note, reviewBucket, reviewNote.Id));
                notifications.Show($"Popped item from {bucket.Name} to {reviewBucket.Name}.");
            }
        });
    }

    private async Task<ZetlClipboardRestoreOutcome> RestoreUserClipboardAfterReplayAsync(
        bool shifted,
        ZetlClipboardSnapshot injected,
        uint injectedToken)
    {
        await delay.WaitAsync(ReplayClipboardRestoreDelay);
        await replayClipboardGate.WaitAsync();
        try
        {
            return await RunOnDispatcherAsync(() => replayClipboard.RestoreIfOwned(
                shifted,
                injected,
                injectedToken));
        }
        finally
        {
            replayClipboardGate.Release();
        }
    }

    private async Task RestoreUserClipboardAfterReplayAndReportAsync(
        bool shifted,
        ZetlClipboardSnapshot injected,
        uint injectedToken)
    {
        var outcome = await RestoreUserClipboardAfterReplayAsync(
            shifted,
            injected,
            injectedToken);
        if (outcome is ZetlClipboardRestoreOutcome.Failed
            or ZetlClipboardRestoreOutcome.FailedClipboardUncertain)
        {
            await RunOnDispatcherAsync(() => ShowReplayRestoreFailure(outcome));
        }
    }

    private void ShowReplayCompletion(
        string bucketName,
        ZetlClipboardRestoreOutcome? restoreOutcome,
        string suffix)
    {
        switch (restoreOutcome)
        {
            case ZetlClipboardRestoreOutcome.OwnershipLost:
                notifications.Show($"{bucketName} replay complete; your newer clipboard was left unchanged.{suffix}");
                break;
            case ZetlClipboardRestoreOutcome.Failed:
                log("Replay completed but restoring the previous clipboard failed without changing the current clipboard.");
                notifications.Show(
                    $"{bucketName} replay complete, but Zetl could not restore your previous clipboard; the current clipboard was left unchanged.{suffix}");
                break;
            case ZetlClipboardRestoreOutcome.FailedClipboardUncertain:
                log("Replay completed but restoring the previous clipboard failed and clipboard integrity is uncertain.");
                notifications.Show(
                    $"{bucketName} replay complete, but Zetl could not fully restore your previous clipboard; the clipboard may have changed.{suffix}");
                break;
            default:
                notifications.Show($"{bucketName} replay complete.{suffix}");
                break;
        }
    }

    private static string ReturnedToSuffix(ZetlReplayFinish finish) =>
        finish.ReturnedTo is { } name ? $" Back to {name}." : "";

    private void ShowReplayRestoreFailure(ZetlClipboardRestoreOutcome outcome)
    {
        log("Replay finished but restoring your previous clipboard failed.");
        notifications.Show(outcome == ZetlClipboardRestoreOutcome.FailedClipboardUncertain
            ? "Replay finished, but Zetl could not fully restore your previous clipboard."
            : "Replay finished without replacing your current clipboard.");
    }

}
