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
    private readonly ZetlClipboardSnapshot?[] replayUserClipboard = new ZetlClipboardSnapshot?[2];
    private readonly ZetlClipboardSnapshot?[] replayInjectedClipboard = new ZetlClipboardSnapshot?[2];
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
    private readonly IImageUrlResolver? imageUrlResolver;

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
        TimeSpan holdDelay,
        IImageUrlResolver? imageUrlResolver = null)
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
        this.imageUrlResolver = imageUrlResolver;
    }

    public async Task OnPhysicalShortcutPassedThroughAsync(
        ChordlEventContext context,
        ZetlCaptureOrigin? captureOrigin = null)
    {
        if (context.KeyCode is not (VK_C or VK_X))
        {
            return;
        }

        var pending = new ZetlPendingShortcut(
            context.KeyCode,
            context.ShiftLane,
            context.ClipboardSequenceNumber,
            captureOrigin);
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

    public async Task<ZetlShortcutRequest?> HandleClaimedHoldAsync(
        ChordlEventContext context,
        ZetlPendingShortcut? pending)
    {
        return context.KeyCode switch
        {
            VK_B => new ZetlBoardRequest(context.ShiftLane),
            VK_C => await CreateCopyHoldRequestAsync(context, pending),
            VK_P => HandlePopToggle(context.ShiftLane),
            VK_R => HandleReplayToggle(context.ShiftLane),
            VK_T => new ZetlTemplatePickerRequest(context.ShiftLane, FromCompileFallback: false),
            VK_X => CreateCutHoldRequest(context, pending),
            VK_V => CreateCompileRequest(context.ShiftLane),
            VK_Z => HandleUndo(context.ShiftLane),
            _ => null
        };
    }

    public ZetlNoteCaptureOutcome CompleteNoteCapture(
        ZetlNoteCaptureRequest request,
        ZetlNoteCaptureResult result)
    {
        if (!result.Committed
            || (request.Image is null && string.IsNullOrWhiteSpace(result.NoteText)))
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

        var note = request.Image is not null
            ? store.AddImageNote(
                noteProject,
                bucket,
                request.Image,
                request.Source,
                request.CaptureOrigin,
                result.NoteText,
                request.ImageSourceUrl)
            : store.AddNote(
                bucket,
                result.NoteText,
                request.Source,
                request.CaptureOrigin);
        undoStack.Push(
            request.Shifted,
            $"Undid save to {bucket.Name}.",
            () => store.DeleteNote(bucket, note.Id));
        if (request.Image is null
            && (!isCut || quickNoteToClipboard())
            && !clipboard.SetText(result.NoteText))
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

        notifications.Show(request.Image is not null
            ? $"Saved image to {ZetlRuntimeLabels.Destination(noteProject, bucket)}."
            : $"Saved to {ZetlRuntimeLabels.Destination(noteProject, bucket)}.");
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

    private async Task<ZetlShortcutRequest?> CreateCopyHoldRequestAsync(
        ChordlEventContext context,
        ZetlPendingShortcut? pending)
    {
        // "Deliberate" so the Start-a-project toggle still shows while on the Journal,
        // which (per Option 1) is itself the active project when nothing else is.
        var hadActiveProject = store.HasDeliberateActiveProject(context.ShiftLane);
        var project = store.GetOrCreateDefaultProject(context.ShiftLane);
        var pendingImage = pending?.ObservedClipboardImage
            ?? TryGetChangedClipboardImage(
                pending?.ClipboardSequenceNumber
                    ?? context.ClipboardSequenceNumber);
        if (pendingImage is { } image)
        {
            var imageBucket = store.GetActiveBucket(context.ShiftLane)
                ?? store.GetScratchBucket(project);
            return new ZetlNoteCaptureRequest(
                context.ShiftLane,
                project,
                imageBucket,
                "",
                "copy",
                ShowStartProjectToggle: !hadActiveProject,
                StartProjectDefault: true,
                ScratchOnlyUntilProjectStarted: false,
                CreateNewProjectToggle: false,
                ProjectToggleText: null,
                ProjectNameDefault: null,
                CaptureOrigin: pending?.CaptureOrigin,
                Image: image);
        }

        var text = ResolveHoldClipboardText(context, pending);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ZetlBoardRequest(context.ShiftLane);
        }

        var resolvedUrl = await TryResolveImageUrlAsync(text);
        if (resolvedUrl is not null)
        {
            var imageBucket = store.GetActiveBucket(context.ShiftLane)
                ?? store.GetScratchBucket(project);
            return new ZetlNoteCaptureRequest(
                context.ShiftLane,
                project,
                imageBucket,
                "",
                "copy",
                ShowStartProjectToggle: !hadActiveProject,
                StartProjectDefault: true,
                ScratchOnlyUntilProjectStarted: false,
                CreateNewProjectToggle: false,
                ProjectToggleText: null,
                ProjectNameDefault: null,
                CaptureOrigin: pending?.CaptureOrigin,
                Image: resolvedUrl.Image,
                ImageSourceUrl: resolvedUrl.SourceUrl);
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
            ProjectNameDefault: null,
            CaptureOrigin: pending?.CaptureOrigin);
    }

    private ZetlShortcutRequest CreateCutHoldRequest(
        ChordlEventContext context,
        ZetlPendingShortcut? pending)
    {
        // "Deliberate" so the Start-a-project toggle still shows while on the Journal,
        // which (per Option 1) is itself the active project when nothing else is.
        var hadActiveProject = store.HasDeliberateActiveProject(context.ShiftLane);
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
            ProjectNameDefault: null,
            CaptureOrigin: pending?.CaptureOrigin);
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
                // No active project and nothing in Scratch to compile: offer Zetl's
                // quick template picker instead. The host decides whether any
                // consumable templates exist, falling back to the original
                // "nothing to compile" message when none do.
                return new ZetlTemplatePickerRequest(shifted, FromCompileFallback: true);
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
        await ObserveClipboardContentAsync(pending, ClipboardObservationTimeout);
    }

    private ZetlClipboardImage? TryGetChangedClipboardImage(uint beforeSequence)
    {
        try
        {
            return clipboard.GetChangeToken() != beforeSequence
                ? clipboard.TryGetImage()
                : null;
        }
        catch (Exception ex)
        {
            log($"Clipboard image read failed: {ex.Message}");
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
            if (clipboard.GetChangeToken() != pending.ClipboardSequenceNumber)
            {
                var image = clipboard.TryGetImage();
                var text = image is null ? clipboard.TryGetText()?.Trim() : null;
                if (image is not null || !string.IsNullOrWhiteSpace(text))
                {
                    pending.SetObservedClipboardContent(text, image);
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

        if (pending.ObservedClipboardText is null
            && pending.ObservedClipboardImage is null)
        {
            await ObserveClipboardContentAsync(pending, AutoCaptureClipboardTimeout);
        }

        var text = pending.ObservedClipboardText;
        var image = pending.ObservedClipboardImage;
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

            var bucket = store.GetActiveBucket(pending.ShiftLane);
            if (bucket is null)
            {
                return;
            }

            var project = store.GetActiveProject(pending.ShiftLane);
            if (project is null)
            {
                return;
            }

            var note = image is not null
                ? store.AddImageNote(
                    project,
                    bucket,
                    image,
                    "copy",
                    pending.CaptureOrigin,
                    sourceUrl: imageSourceUrl)
                : store.AddNote(
                    bucket,
                    text!,
                    "copy",
                    pending.CaptureOrigin);
            undoStack.Push(
                pending.ShiftLane,
                image is not null
                    ? $"Undid image capture to {bucket.Name}."
                    : $"Undid capture to {bucket.Name}.",
                () => store.DeleteNote(bucket, note.Id));
            notifications.Show(image is not null
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
        var replayItem = fifoNote.IsImage
            ? project is not null && store.ReadImageAsset(project, fifoNote) is { } bytes
                ? ZetlClipboardSnapshot.FromImage(new ZetlClipboardImage(
                    bytes,
                    fifoNote.Image!.Width,
                    fifoNote.Image.Height))
                : null
            : ZetlClipboardSnapshot.FromText(fifoNote.Text);
        var bucketName = activeBucket.Name;
        if (replayItem is null)
        {
            notifications.Show($"Paste failed; {bucketName} image asset is unavailable.");
            return;
        }

        RememberUserClipboardBeforeReplay(shifted);
        // If the clipboard write itself fails, don't paste -- the foreground app
        // would receive whatever stale text was there instead of the replay item.
        if (!SetReplayClipboard(shifted, replayItem))
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
                () => RestoreUserClipboardAfterReplayAsync(shifted, replayItem), "replay clipboard restore", log);
            notifications.Show(replayComplete
                ? $"{bucketName} replay complete."
                : $"Pasted next item from {bucketName}.");
        });
    }

    private async Task HandlePopTapAsync(bool shifted)
    {
        await delay.WaitAsync(PopClipboardDelay);
        var image = clipboard.TryGetImage();
        var text = image is null ? clipboard.TryGetText() : null;
        if (text is null && image is null)
        {
            return;
        }

        dispatcher.Post(() =>
        {
            var popped = image is not null
                ? store.TryPopLastMatchingActiveImage(
                    Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(image.PngBytes))
                        .ToLowerInvariant(),
                    shifted,
                    out var bucket,
                    out var note)
                : store.TryPopLastMatchingActiveNote(
                    text!,
                    shifted,
                    out bucket,
                    out note);
            if (popped
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
        var current = ReadClipboardSnapshot();
        if (!ZetlClipboardSnapshot.ContentEquals(
                current,
                replayInjectedClipboard[index]))
        {
            replayUserClipboard[index] = current;
        }
    }

    private bool SetReplayClipboard(bool shifted, ZetlClipboardSnapshot item)
    {
        if (!ZetlClipboardSnapshot.ContentEquals(ReadClipboardSnapshot(), item)
            && !WriteClipboardSnapshot(item))
        {
            return false;
        }

        replayInjectedClipboard[shifted ? 1 : 0] = item;
        return true;
    }

    private async Task RestoreUserClipboardAfterReplayAsync(
        bool shifted,
        ZetlClipboardSnapshot injected)
    {
        var index = shifted ? 1 : 0;
        var restoreTo = replayUserClipboard[index];
        if (restoreTo is null)
        {
            return;
        }

        await delay.WaitAsync(ReplayClipboardRestoreDelay);
        dispatcher.Post(() =>
        {
            if (ZetlClipboardSnapshot.ContentEquals(
                    ReadClipboardSnapshot(),
                    injected))
            {
                // Only mark the clipboard as restored if the write actually took;
                // otherwise the tracking would lie about what's on the clipboard.
                if (WriteClipboardSnapshot(restoreTo))
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

    private ZetlClipboardSnapshot? ReadClipboardSnapshot()
    {
        var image = clipboard.TryGetImage();
        if (image is not null)
        {
            return ZetlClipboardSnapshot.FromImage(image);
        }

        var text = clipboard.TryGetText();
        return string.IsNullOrEmpty(text) ? null : ZetlClipboardSnapshot.FromText(text);
    }

    private bool WriteClipboardSnapshot(ZetlClipboardSnapshot snapshot)
    {
        return snapshot.Image is not null
            ? clipboard.SetImage(snapshot.Image)
            : clipboard.SetText(snapshot.Text ?? "");
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
    private ZetlClipboardImage? observedClipboardImage;
    private bool cancelled;

    public ZetlPendingShortcut(
        int keyCode,
        bool shiftLane,
        uint clipboardSequenceNumber,
        ZetlCaptureOrigin? captureOrigin = null)
    {
        KeyCode = keyCode;
        ShiftLane = shiftLane;
        ClipboardSequenceNumber = clipboardSequenceNumber;
        CaptureOrigin = captureOrigin;
    }

    public int KeyCode { get; }

    public bool ShiftLane { get; }

    public uint ClipboardSequenceNumber { get; }

    public ZetlCaptureOrigin? CaptureOrigin { get; }

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

    public ZetlClipboardImage? ObservedClipboardImage
    {
        get
        {
            lock (gate)
            {
                return observedClipboardImage;
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

    public void SetObservedClipboardContent(string? text, ZetlClipboardImage? image)
    {
        lock (gate)
        {
            observedClipboardText = text;
            observedClipboardImage = image;
        }
    }
}

internal sealed record ZetlClipboardSnapshot(
    string? Text,
    ZetlClipboardImage? Image,
    string Fingerprint)
{
    public static ZetlClipboardSnapshot FromText(string text) =>
        new(text, null, $"text:{text.Trim()}");

    public static ZetlClipboardSnapshot FromImage(ZetlClipboardImage image) =>
        new(
            null,
            image,
            "image:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(image.PngBytes)));

    public static bool ContentEquals(
        ZetlClipboardSnapshot? left,
        ZetlClipboardSnapshot? right) =>
        left is null ? right is null : right is not null
            && string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.Ordinal);
}
