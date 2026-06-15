using Chordl;
using static Chordl.ChordlKeys;

namespace ZETL;

internal sealed class ZetlShortcutCoordinator
{
    private static readonly TimeSpan ClipboardPollInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan ClipboardObservationTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AutoCaptureClipboardTimeout = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan PopClipboardDelay = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan ReplayClipboardRestoreDelay = TimeSpan.FromMilliseconds(150);

    private readonly object pendingGate = new();
    private readonly Dictionary<(int KeyCode, bool Shifted), ZetlPendingShortcut> pendingShortcuts = new();
    private readonly string?[] replayUserClipboard = new string?[2];
    private readonly string?[] replayInjectedClipboard = new string?[2];
    private readonly ZetlStateStore store;
    private readonly IKeyboardBackend keyboard;
    private readonly IClipboard clipboard;
    private readonly IZetlDispatcher dispatcher;
    private readonly IZetlDelay delay;
    private readonly IZetlNotificationSink notifications;
    private readonly ZetlUndoStack undoStack;
    private readonly Func<bool> autoCaptureOnCopy;
    private readonly Func<bool> quickNoteToClipboard;
    private readonly Action<string> log;
    private readonly TimeSpan holdDelay;

    public ZetlShortcutCoordinator(
        ZetlStateStore store,
        IKeyboardBackend keyboard,
        IClipboard clipboard,
        IZetlDispatcher dispatcher,
        IZetlDelay delay,
        IZetlNotificationSink notifications,
        ZetlUndoStack undoStack,
        Func<bool> autoCaptureOnCopy,
        Func<bool> quickNoteToClipboard,
        Action<string> log,
        TimeSpan holdDelay)
    {
        this.store = store;
        this.keyboard = keyboard;
        this.clipboard = clipboard;
        this.dispatcher = dispatcher;
        this.delay = delay;
        this.notifications = notifications;
        this.undoStack = undoStack;
        this.autoCaptureOnCopy = autoCaptureOnCopy;
        this.quickNoteToClipboard = quickNoteToClipboard;
        this.log = log;
        this.holdDelay = holdDelay;
    }

    public async Task OnPhysicalShortcutPassedThroughAsync(ChordlEventContext context)
    {
        if (context.KeyCode is not (VK_C or VK_X))
        {
            return;
        }

        var pending = new ZetlPendingShortcut(
            context.KeyCode,
            context.ShiftLane,
            context.ClipboardSequenceNumber);
        lock (pendingGate)
        {
            pendingShortcuts[PendingKey(context.KeyCode, context.ShiftLane)] = pending;
        }

        var observeTask = ObserveClipboardChangeAsync(pending);
        var autoCaptureTask = context.KeyCode == VK_C && autoCaptureOnCopy()
            ? AutoCaptureCopyAsync(pending)
            : Task.CompletedTask;
        await Task.WhenAll(observeTask, autoCaptureTask);
    }

    public ZetlPendingShortcut? CancelPending(int keyCode, bool shifted)
    {
        lock (pendingGate)
        {
            var key = PendingKey(keyCode, shifted);
            if (!pendingShortcuts.Remove(key, out var pending))
            {
                return null;
            }

            pending.Cancel();
            return pending;
        }
    }

    public ZetlPendingShortcut? ClaimPendingForHold(
        ChordlEventContext context)
    {
        return CancelPending(context.KeyCode, context.ShiftLane);
    }

    public bool OnTapDispatched(ChordlEventContext context)
    {
        if (context.KeyCode != VK_V || context.ReplayShift)
        {
            return false;
        }

        var activeBucket = store.GetActiveBucket(context.ShiftLane);
        if (activeBucket is not null && ZetlStateStore.IsFifoBucket(activeBucket))
        {
            // The hook callback needs the handled decision synchronously, but the
            // replay clipboard read/write and synthetic paste must not run on the
            // low-level keyboard hook thread: blocking it stalls system-wide input
            // and can make Windows silently remove the hook. Enqueue that work
            // onto the dispatcher and return the decision immediately.
            var shiftLane = context.ShiftLane;
            dispatcher.Post(() => ZetlAsync.RunLogged(
                () => HandleReplayTapAsync(shiftLane, activeBucket), "replay tap", log));
            return true;
        }

        ZetlAsync.RunLogged(() => HandlePopTapAsync(context.ShiftLane), "pop tap", log);
        return false;
    }

    public Task<ZetlShortcutRequest?> HandleHoldAsync(ChordlEventContext context)
    {
        return HandleClaimedHoldAsync(
            context,
            ClaimPendingForHold(context));
    }

    // Hold handling is fully synchronous now that the clipboard is observed
    // ahead of time into the pending shortcut; the Task return type is kept so
    // the UI-thread callers can keep awaiting it.
    public Task<ZetlShortcutRequest?> HandleClaimedHoldAsync(
        ChordlEventContext context,
        ZetlPendingShortcut? pending)
    {
        ZetlShortcutRequest? request = context.KeyCode switch
        {
            VK_B => new ZetlBoardRequest(context.ShiftLane),
            VK_C => CreateCopyHoldRequest(context, pending),
            VK_P => HandlePopToggle(context.ShiftLane),
            VK_R => HandleReplayToggle(context.ShiftLane),
            VK_X => CreateCutHoldRequest(context, pending),
            VK_V => CreateCompileRequest(context.ShiftLane),
            VK_Z => HandleUndo(context.ShiftLane),
            _ => null
        };
        return Task.FromResult(request);
    }

    public ZetlNoteCaptureOutcome CompleteNoteCapture(
        ZetlNoteCaptureRequest request,
        ZetlNoteCaptureResult result)
    {
        if (!result.Committed || string.IsNullOrWhiteSpace(result.NoteText))
        {
            if (request.ShowStartProjectToggle && !request.CreateNewProjectToggle)
            {
                store.ClearActiveProject(request.Shifted);
            }

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

        var note = store.AddNote(bucket, result.NoteText, request.Source);
        undoStack.Push(
            request.Shifted,
            $"Undid save to {bucket.Name}.",
            () => store.DeleteNote(bucket, note.Id));
        if ((!isCut || quickNoteToClipboard()) && !clipboard.SetText(result.NoteText))
        {
            // The note is already saved; don't roll it back, just record that the
            // clipboard didn't pick up the saved text.
            log($"Saved note to {bucket.Name}, but copying it to the clipboard failed.");
        }

        // The Activate toggle decides the lane's active project. When off, undo
        // the dated default's auto-activation (nothing was active before) and
        // deactivate the chosen project if it is the one currently active;
        // otherwise leave the prior active project untouched.
        if (result.StartProject)
        {
            store.SetActiveProject(noteProject.Id, request.Shifted);
        }
        else if (request.ShowStartProjectToggle
            || store.GetActiveProject(request.Shifted)?.Id == noteProject.Id)
        {
            store.ClearActiveProject(request.Shifted);
        }

        notifications.Show($"Saved to {ZetlRuntimeLabels.Destination(noteProject, bucket)}.");
        return ZetlNoteCaptureOutcome.None;
    }

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
                var note = store.AddNote(destination, result.CompiledText, "compile");
                undoStack.Push(
                    request.Shifted,
                    $"Undid compile to {destination.Name}.",
                    () => store.DeleteNote(destination, note.Id));
                savedSummary = $"to {destinationLabel}";
            }
            else
            {
                var notes = store.AddNotes(destination, result.SelectedNoteTexts, "compile");
                undoStack.Push(
                    request.Shifted,
                    $"Undid compile to {destination.Name}.",
                    () =>
                    {
                        foreach (var note in notes)
                        {
                            store.DeleteNote(destination, note.Id);
                        }
                    });
                savedSummary = $"{notes.Count} notes to {destinationLabel}";
            }

            notifications.Show($"Compiled {savedSummary}.");
            return ZetlCompileOutcome.RestoreTarget;
        }

        if (!clipboard.SetText(result.CompiledText))
        {
            // Staging failed, so don't paste stale clipboard content or claim a
            // copy succeeded. The compiled text can be re-produced by compiling
            // again.
            notifications.Show("Couldn't copy the compiled text to the clipboard.");
            return ZetlCompileOutcome.RestoreTarget;
        }

        if (result.PasteNow)
        {
            return ZetlCompileOutcome.PasteNow;
        }

        notifications.Show("Copied compiled text to clipboard.");
        return ZetlCompileOutcome.RestoreTarget;
    }

    public async Task PasteCompiledTextAsync()
    {
        await delay.WaitAsync(PopClipboardDelay);
        var pasted = await keyboard.SendPaste();
        dispatcher.Post(() => notifications.Show(pasted
            ? "Pasted compiled text."
            : "Paste failed; compiled text remains on the clipboard. If the target is elevated, run Zetl elevated too."));
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

    public async Task<string?> WaitForClipboardTextAsync(uint beforeSequence, TimeSpan timeout)
    {
        var elapsed = TimeSpan.Zero;
        do
        {
            if (clipboard.GetChangeToken() != beforeSequence)
            {
                var text = clipboard.TryGetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }

            await delay.WaitAsync(ClipboardPollInterval);
            elapsed += ClipboardPollInterval;
        }
        while (elapsed < timeout);

        return null;
    }

    public void ResetReplayClipboardTracking(bool shifted)
    {
        var index = shifted ? 1 : 0;
        replayUserClipboard[index] = null;
        replayInjectedClipboard[index] = null;
    }

    private ZetlShortcutRequest? CreateCopyHoldRequest(
        ChordlEventContext context,
        ZetlPendingShortcut? pending)
    {
        var hadActiveProject = store.GetActiveProject(context.ShiftLane) is not null;
        var project = store.GetOrCreateDefaultProject(context.ShiftLane);
        var text = ResolveHoldClipboardText(context, pending);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ZetlBoardRequest(context.ShiftLane);
        }

        // Held copy capture shares the quick-note dialog: same project selector,
        // Activate toggle, and bucket access. It differs by preferring the active
        // bucket as the default destination, activating the project by default
        // (StartProjectDefault), and keeping the clipboard in sync with the saved
        // note (handled in CompleteNoteCapture).
        var preferredBucket = store.GetActiveBucket(context.ShiftLane)
            ?? store.GetScratchBucket(project);
        return new ZetlNoteCaptureRequest(
            context.ShiftLane,
            project,
            preferredBucket,
            text,
            "copy",
            ShowStartProjectToggle: !hadActiveProject,
            StartProjectDefault: true,
            ScratchOnlyUntilProjectStarted: false,
            CreateNewProjectToggle: false,
            ProjectToggleText: null,
            ProjectNameDefault: null);
    }

    private ZetlShortcutRequest CreateCutHoldRequest(
        ChordlEventContext context,
        ZetlPendingShortcut? pending)
    {
        var hadActiveProject = store.GetActiveProject(context.ShiftLane) is not null;
        var project = store.GetOrCreateDefaultProject(context.ShiftLane);
        var scratch = store.GetScratchBucket(project);
        var preferredBucket = hadActiveProject ? store.GetQuickNoteBucket(project) : scratch;
        var text = ResolveHoldClipboardText(context, pending);
        return new ZetlNoteCaptureRequest(
            context.ShiftLane,
            project,
            preferredBucket,
            text,
            "cut",
            ShowStartProjectToggle: !hadActiveProject,
            StartProjectDefault: false,
            ScratchOnlyUntilProjectStarted: !hadActiveProject,
            CreateNewProjectToggle: false,
            ProjectToggleText: null,
            ProjectNameDefault: null);
    }

    // The text a held copy/cut should capture: the clipboard value already
    // observed for this pending shortcut, falling back to a fresh changed-text
    // read, then to empty.
    private string ResolveHoldClipboardText(
        ChordlEventContext context,
        ZetlPendingShortcut? pending)
    {
        return pending?.ObservedClipboardText
            ?? TryGetChangedClipboardText(
                pending?.ClipboardSequenceNumber
                    ?? context.ClipboardSequenceNumber)
            ?? "";
    }

    private string? TryGetChangedClipboardText(uint beforeSequence)
    {
        if (clipboard.GetChangeToken() == beforeSequence)
        {
            return null;
        }

        var text = clipboard.TryGetText();
        return string.IsNullOrWhiteSpace(text)
            ? null
            : text.Trim();
    }

    private ZetlShortcutRequest? CreateCompileRequest(bool shifted)
    {
        var project = store.GetActiveProject(shifted);
        if (project is null)
        {
            if (!store.TryGetScratchCompileTarget(
                    out project,
                    out var scratchBucket,
                    shifted)
                || project is null
                || scratchBucket is null)
            {
                notifications.Show("No Zetl notes to compile yet.");
                return null;
            }
        }
        else if (!store.HasCompilableNotes(project))
        {
            notifications.Show("No Zetl notes to compile yet.");
            return null;
        }

        return new ZetlCompileRequest(shifted, project, BucketScope: null);
    }

    private ZetlShortcutRequest? HandlePopToggle(bool shifted)
    {
        var bucket = store.GetActiveBucket(shifted);
        if (bucket is null)
        {
            notifications.Show("No active bucket yet.");
            return null;
        }

        if (ZetlStateStore.IsFifoBucket(bucket))
        {
            store.SetBucketKind(bucket, "Standard");
            store.SetBucketPopMode(bucket, true);
            notifications.Show($"{bucket.Name} pop is on.");
            return null;
        }

        store.ToggleActiveBucketPopMode(shifted);
        notifications.Show($"{bucket.Name} pop is {(bucket.PopMode ? "on" : "off")}.");
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

        if (ZetlStateStore.IsFifoBucket(bucket))
        {
            store.SetBucketKind(bucket, "Standard");
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
        pending.SetObservedClipboardText(await WaitForClipboardTextAsync(
            pending.ClipboardSequenceNumber,
            ClipboardObservationTimeout));
    }

    private async Task AutoCaptureCopyAsync(ZetlPendingShortcut pending)
    {
        await delay.WaitAsync(holdDelay + TimeSpan.FromMilliseconds(25));
        if (pending.Cancelled)
        {
            return;
        }

        var text = pending.ObservedClipboardText
            ?? await WaitForClipboardTextAsync(
                pending.ClipboardSequenceNumber,
                AutoCaptureClipboardTimeout);
        if (text is null || pending.Cancelled)
        {
            return;
        }

        dispatcher.Post(() =>
        {
            if (pending.Cancelled)
            {
                return;
            }

            var bucket = store.GetActiveBucket(pending.ShiftLane);
            if (bucket is null)
            {
                return;
            }

            var project = store.GetActiveProject(pending.ShiftLane);
            var note = store.AddNote(bucket, text, "copy");
            undoStack.Push(
                pending.ShiftLane,
                $"Undid capture to {bucket.Name}.",
                () => store.DeleteNote(bucket, note.Id));
            notifications.Show($"Captured to {ZetlRuntimeLabels.Destination(project, bucket)}.");
        });
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
        if (!store.TryPeekNextFifoNote(activeBucket, out var fifoNote) || fifoNote is null)
        {
            // The bucket is empty, so replay is genuinely done -- return to
            // Standard regardless. But this tap suppressed the physical Ctrl+V, so
            // send the user's own clipboard through as the final pass-through and
            // report if even that paste didn't land (e.g. an elevated target).
            var emptyBucketName = activeBucket.Name;
            store.SetBucketKind(activeBucket, "Standard");
            var finalPasted = await keyboard.SendPaste();
            dispatcher.Post(() => notifications.Show(finalPasted
                ? $"{emptyBucketName} replay complete."
                : $"{emptyBucketName} replay complete, but the final paste didn't land."));
            return;
        }

        var project = store.GetActiveProject(shifted);
        var noteId = fifoNote.Id;
        var noteText = fifoNote.Text;
        var bucketName = activeBucket.Name;
        RememberUserClipboardBeforeReplay(shifted);
        // If the clipboard write itself fails, don't paste -- the foreground app
        // would receive whatever stale text was there instead of the replay item.
        if (!SetReplayClipboard(shifted, noteText))
        {
            notifications.Show($"Paste failed; {bucketName} item kept.");
            return;
        }

        // Await the actual injection result, not just that the paste was queued.
        var pasted = await keyboard.SendPaste();

        // The await may resume off the dispatcher thread, so marshal the store
        // mutations back through the dispatcher.
        dispatcher.Post(() =>
        {
            if (!pasted)
            {
                notifications.Show($"Paste failed; {bucketName} item kept.");
                return;
            }

            ZetlBucket? reviewBucket = null;
            ZetlNote? consumedNote = null;
            ZetlNote? reviewNote = null;
            var consumed = project is not null
                ? store.TryConsumeFifoNoteToReview(
                    project,
                    activeBucket,
                    noteId,
                    out reviewBucket,
                    out consumedNote,
                    out reviewNote)
                : store.TryConsumeFifoNote(activeBucket, noteId, out consumedNote);
            if (consumed && reviewBucket is not null)
            {
                log($"Archived replay paste from {bucketName} to {reviewBucket.Name}.");
            }

            if (consumed && consumedNote is not null)
            {
                var undoReviewBucket = reviewBucket;
                var undoReviewNoteId = reviewNote?.Id;
                undoStack.Push(
                    shifted,
                    $"Restored replay item to {bucketName}.",
                    () => store.RestoreFifoConsumedNote(
                        activeBucket,
                        consumedNote,
                        undoReviewBucket,
                        undoReviewNoteId));
            }

            var replayComplete = !store.TryPeekNextFifoNote(activeBucket, out _);
            if (replayComplete)
            {
                store.SetBucketKind(activeBucket, "Standard");
            }

            ZetlAsync.RunLogged(
                () => RestoreUserClipboardAfterReplayAsync(shifted, noteText), "replay clipboard restore", log);
            notifications.Show(replayComplete
                ? $"{bucketName} replay complete."
                : $"Pasted next item from {bucketName}.");
        });
    }

    private async Task HandlePopTapAsync(bool shifted)
    {
        await delay.WaitAsync(PopClipboardDelay);
        var text = clipboard.TryGetText();
        if (text is null)
        {
            return;
        }

        dispatcher.Post(() =>
        {
            if (store.TryPopLastMatchingActiveNote(
                    text,
                    shifted,
                    out var bucket,
                    out var note)
                && bucket is not null
                && note is not null)
            {
                undoStack.Push(
                    shifted,
                    $"Restored popped note to {bucket.Name}.",
                    () => store.RestoreNote(bucket, note));
                notifications.Show("Popped the pasted item from the active bucket.");
            }
        });
    }

    private void RememberUserClipboardBeforeReplay(bool shifted)
    {
        var index = shifted ? 1 : 0;
        var current = clipboard.TryGetText();
        if (!string.Equals(
                current?.Trim(),
                replayInjectedClipboard[index]?.Trim(),
                StringComparison.Ordinal))
        {
            replayUserClipboard[index] = current;
        }
    }

    private bool SetReplayClipboard(bool shifted, string text)
    {
        if (!string.Equals(
                clipboard.TryGetText()?.Trim(),
                text.Trim(),
                StringComparison.Ordinal)
            && !clipboard.SetText(text))
        {
            return false;
        }

        replayInjectedClipboard[shifted ? 1 : 0] = text;
        return true;
    }

    private async Task RestoreUserClipboardAfterReplayAsync(bool shifted, string injectedText)
    {
        var index = shifted ? 1 : 0;
        var restoreTo = replayUserClipboard[index];
        if (string.IsNullOrEmpty(restoreTo))
        {
            return;
        }

        await delay.WaitAsync(ReplayClipboardRestoreDelay);
        dispatcher.Post(() =>
        {
            if (string.Equals(
                    clipboard.TryGetText()?.Trim(),
                    injectedText.Trim(),
                    StringComparison.Ordinal))
            {
                // Only mark the clipboard as restored if the write actually took;
                // otherwise the tracking would lie about what's on the clipboard.
                if (clipboard.SetText(restoreTo))
                {
                    replayInjectedClipboard[index] = restoreTo;
                }
                else
                {
                    log("Replay finished but restoring your previous clipboard failed.");
                }
            }
        });
    }

    private static (int KeyCode, bool Shifted) PendingKey(int keyCode, bool shifted)
    {
        return (keyCode, shifted);
    }
}

internal sealed class ZetlPendingShortcut
{
    private readonly object gate = new();
    private string? observedClipboardText;
    private bool cancelled;

    public ZetlPendingShortcut(
        int keyCode,
        bool shiftLane,
        uint clipboardSequenceNumber)
    {
        KeyCode = keyCode;
        ShiftLane = shiftLane;
        ClipboardSequenceNumber = clipboardSequenceNumber;
    }

    public int KeyCode { get; }

    public bool ShiftLane { get; }

    public uint ClipboardSequenceNumber { get; }

    public string? ObservedClipboardText
    {
        get
        {
            lock (gate)
            {
                return observedClipboardText;
            }
        }
    }

    public bool Cancelled
    {
        get
        {
            lock (gate)
            {
                return cancelled;
            }
        }
    }

    public void Cancel()
    {
        lock (gate)
        {
            cancelled = true;
        }
    }

    public void SetObservedClipboardText(string? text)
    {
        lock (gate)
        {
            observedClipboardText = text;
        }
    }
}
