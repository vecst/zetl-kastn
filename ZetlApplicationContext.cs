using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Program;

namespace ZETL;

internal sealed class ZetlApplicationContext : ApplicationContext
{
    private const int MaxUndoActions = 100;
    private const int ReplayClipboardRestoreDelayMs = 150;
    private const int LogFlushIntervalMs = 5000;
    private const int LogRetentionDays = 14;
    private const int LogMaxNotesPerDay = 2000;
    private readonly ZetlStateStore store = new();
    private readonly ZetlAppSettingsStore appSettings = new();
    private readonly NotifyIcon trayIcon;
    private readonly Control invoker = new();
    private readonly ZetlToastService toastService = new();
    private readonly Dictionary<(int KeyCode, bool Shifted), PendingShortcut> pendingShortcuts = new();
    private readonly List<ZetlUndoAction> undoStack = new();
    private readonly List<string> logLines = new();
    // Replay borrows the system clipboard for each paste; these remember the
    // user's own clipboard per lane ([0] normal, [1] Shift) so it can be put back
    // afterwards. replayInjected tracks the value we last wrote, so a clipboard
    // the user changed mid-replay is recognized rather than treated as ours.
    private readonly string?[] replayUserClipboard = new string?[2];
    private readonly string?[] replayInjectedClipboard = new string?[2];
    // Activity-log lines buffered between flushes so persisting them never sits
    // on the per-keystroke path; a timer drains this into the "Zetl Logs" project.
    private readonly List<string> pendingLogNotes = new();
    private System.Windows.Forms.Timer? logFlushTimer;
    private BoardForm? boardForm;

    private readonly TimeSpan holdDelay;

    public ZetlApplicationContext(TimeSpan holdDelay)
    {
        this.holdDelay = holdDelay;
        ApplyAppSettings();
        store.ConsolidateDefaultProject();
        store.ConsolidateDefaultProject(shifted: true);
        store.ClearActiveProject();
        store.ClearActiveProject(shifted: true);
        _ = invoker.Handle;
        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Zetl",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        trayIcon.DoubleClick += (_, _) => ShowBoard();
        logFlushTimer = new System.Windows.Forms.Timer { Interval = LogFlushIntervalMs };
        logFlushTimer.Tick += (_, _) => FlushLogNotes();
        logFlushTimer.Start();
        BeginInvoke(ShowFirstRunIfNeeded);
    }

    public void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        logLines.Add(line);
        if (logLines.Count > 200)
        {
            logLines.RemoveAt(0);
        }
    }

    public void OnPhysicalShortcutPassedThrough(ChordlEventContext context)
    {
        if (context.KeyCode is not (VK_C or VK_X))
        {
            return;
        }

        var pending = new PendingShortcut(context.KeyCode, context.ShiftLane, context.ClipboardSequenceNumber);
        lock (pendingShortcuts)
        {
            pendingShortcuts[PendingKey(context.KeyCode, context.ShiftLane)] = pending;
        }

        BeginInvoke(async () => await ObserveClipboardChangeAsync(pending));

        if (context.KeyCode == VK_C && appSettings.Settings.AutoCaptureOnCopy)
        {
            BeginInvoke(async () => await AutoCaptureCopyAsync(pending));
        }
    }

    private void ApplyAppSettings()
    {
        var settings = appSettings.Settings;
        toastService.DisplayMilliseconds = settings.ToastDisplayMs;
        var projectBuckets = settings.DefaultProjectBuckets.Count > 0
            ? settings.DefaultProjectBuckets.ToList()
            : new List<string> { "Inbox", "Scratch" };
        store.Defaults = new ZetlBucketDefaults(projectBuckets, settings.DefaultCompileMode, settings.DefaultTsvRowLength);
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
            if (store.TryPeekNextFifoNote(activeBucket, out var fifoNote) && fifoNote is not null)
            {
                var lane = context.ShiftLane;
                var project = store.GetActiveProject(lane);
                var noteId = fifoNote.Id;
                var noteText = fifoNote.Text;
                var bucketName = activeBucket.Name;

                // Replay only borrows the clipboard for the paste: remember what
                // the user actually had on it, swap in the queue item, then put
                // their value back once the paste lands. A plain Ctrl+V then still
                // pastes the user's real last copy instead of a replay leftover.
                RememberUserClipboardBeforeReplay(lane);
                SetReplayClipboard(lane, noteText);
                if (!ChordlInput.SendPaste(Log))
                {
                    BeginInvoke(() => ShowInfo($"Paste failed; {bucketName} item kept."));
                    return true;
                }

                BeginInvoke(() =>
                {
                    ZetlBucket? reviewBucket = null;
                    ZetlNote? consumedNote = null;
                    ZetlNote? reviewNote = null;
                    var consumed = project is not null
                        ? store.TryConsumeFifoNoteToReview(project, activeBucket, noteId, out reviewBucket, out consumedNote, out reviewNote)
                        : store.TryConsumeFifoNote(activeBucket, noteId, out consumedNote);
                    if (consumed && reviewBucket is not null)
                    {
                        Log($"Archived replay paste from {bucketName} to {reviewBucket.Name}.");
                    }

                    if (consumed && consumedNote is not null)
                    {
                        var undoReviewBucket = reviewBucket;
                        var undoReviewNoteId = reviewNote?.Id;
                        PushUndo(
                            lane,
                            $"Restored replay item to {bucketName}.",
                            () => store.RestoreFifoConsumedNote(activeBucket, consumedNote, undoReviewBucket, undoReviewNoteId));
                    }

                    var replayComplete = !store.TryPeekNextFifoNote(activeBucket, out _);
                    if (replayComplete)
                    {
                        store.SetBucketKind(activeBucket, "Standard");
                    }

                    RestoreUserClipboardAfterReplay(lane, noteText);
                    ShowInfo(replayComplete
                        ? $"{bucketName} replay complete."
                        : $"Pasted next item from {bucketName}.");
                });
                return true;
            }

            BeginInvoke(() =>
            {
                store.SetBucketKind(activeBucket, "Standard");
                ChordlInput.SendPaste(Log);
                ShowInfo($"{activeBucket.Name} replay complete.");
            });
            return true;
        }

        BeginInvoke(async () =>
        {
            await Task.Delay(75);
            var text = ClipboardText.TryGet();
            if (text is not null
                && store.TryPopLastMatchingActiveNote(text, context.ShiftLane, out var bucket, out var note)
                && bucket is not null
                && note is not null)
            {
                PushUndo(
                    context.ShiftLane,
                    $"Restored popped note to {bucket.Name}.",
                    () => store.RestoreNote(bucket, note));
                ShowInfo("Popped the pasted item from the active bucket.");
            }
        });
        return false;
    }

    // Snapshot the user's real clipboard before a replay paste borrows it. If the
    // clipboard isn't the item we last injected for this lane, the user (or the
    // target app) put it there, so it is their value to restore to later.
    private void RememberUserClipboardBeforeReplay(bool shifted)
    {
        var index = shifted ? 1 : 0;
        var current = ClipboardText.TryGet();
        if (!string.Equals(current?.Trim(), replayInjectedClipboard[index]?.Trim(), StringComparison.Ordinal))
        {
            replayUserClipboard[index] = current;
        }
    }

    private void SetReplayClipboard(bool shifted, string text)
    {
        SetClipboardTextIfDifferent(text);
        replayInjectedClipboard[shifted ? 1 : 0] = text;
    }

    private void RestoreUserClipboardAfterReplay(bool shifted, string injectedText)
    {
        var index = shifted ? 1 : 0;
        var restoreTo = replayUserClipboard[index];
        if (string.IsNullOrEmpty(restoreTo))
        {
            // Nothing meaningful to put back; leave the replay item on the
            // clipboard rather than blanking it.
            return;
        }

        BeginInvoke(async () =>
        {
            // Let the target app consume the paste before restoring, otherwise it
            // could read the restored value instead of the replay item.
            await Task.Delay(ReplayClipboardRestoreDelayMs);
            // Only restore if our injected item is still on the clipboard; if the
            // user copied something new since, leave their new copy in place.
            if (string.Equals(ClipboardText.TryGet()?.Trim(), injectedText.Trim(), StringComparison.Ordinal))
            {
                ClipboardText.Set(restoreTo);
                replayInjectedClipboard[index] = restoreTo;
            }
        });
    }

    private void ResetReplayClipboardTracking(bool shifted)
    {
        var index = shifted ? 1 : 0;
        replayUserClipboard[index] = null;
        replayInjectedClipboard[index] = null;
    }

    // "<bucket> in <project>" for save toasts, so it's clear which project (and
    // therefore which lane) a note landed in.
    private static string DestinationLabel(ZetlProject? project, ZetlBucket bucket)
    {
        return project is null ? bucket.Name : $"{bucket.Name} in {project.Name}";
    }

    private static void SetClipboardTextIfDifferent(string text)
    {
        if (!string.Equals(ClipboardText.TryGet()?.Trim(), text.Trim(), StringComparison.Ordinal))
        {
            ClipboardText.Set(text);
        }
    }

    public void OnHoldDetected(ChordlEventContext context)
    {
        var targetWindow = GetForegroundWindow();
        BeginInvoke(async () => await HandleHoldAsync(context, targetWindow));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            logFlushTimer?.Stop();
            logFlushTimer?.Dispose();
            FlushLogNotes();
            boardForm?.Dispose();
            toastService.Dispose();
            trayIcon.Dispose();
            invoker.Dispose();
        }

        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Board", null, (_, _) => ShowBoard());
        menu.Items.Add("New Project", null, (_, _) => ShowProjectSetupDialog());
        menu.Items.Add("How Zetl Works", null, (_, _) => ShowFirstRunGuide(markSeen: false));
        menu.Items.Add("Notification History", null, (_, _) => toastService.ShowHistory());
        menu.Items.Add("Clear Notification History", null, (_, _) => toastService.ClearHistory());
        menu.Items.Add("Toggle Active Bucket Pop Mode", null, (_, _) =>
        {
            if (store.ActiveBucket is { } activeBucket && ZetlStateStore.IsFifoBucket(activeBucket))
            {
                ShowInfo("Replay buckets cannot use pop mode.");
                return;
            }

            store.ToggleActiveBucketPopMode();
            var bucket = store.ActiveBucket;
            ShowInfo(bucket is null
                ? "No active bucket yet."
                : $"{bucket.Name} pop mode is {(bucket.PopMode ? "on" : "off")}.");
        });
        menu.Items.Add("Settings", null, (_, _) => ShowSettingsDialog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
        return menu;
    }

    private void ShowSettingsDialog()
    {
        using var form = new ZetlSettingsForm(appSettings.Settings);
        if (ZetlDialogPlacement.ShowForegroundDialog(form) != DialogResult.OK)
        {
            return;
        }

        var settings = appSettings.Settings;
        settings.ToastDisplayMs = form.ToastDisplayMs;
        settings.AutoCaptureOnCopy = form.AutoCaptureOnCopy;
        settings.DefaultProjectBuckets = form.DefaultProjectBuckets.Count > 0
            ? form.DefaultProjectBuckets
            : new List<string> { "Inbox", "Scratch" };
        settings.DefaultCompileMode = form.DefaultCompileMode;
        settings.DefaultTsvRowLength = form.DefaultTsvRowLength;
        appSettings.Save();
        ApplyAppSettings();
        ShowInfo("Settings saved.");
    }

    private void ShowFirstRunIfNeeded()
    {
        if (appSettings.Settings.HasSeenFirstRun)
        {
            return;
        }

        ShowFirstRunGuide(markSeen: true);
    }

    private void ShowFirstRunGuide(bool markSeen)
    {
        using var form = new FirstRunForm();
        var result = ZetlDialogPlacement.ShowForegroundDialog(form);
        if (markSeen)
        {
            appSettings.MarkFirstRunSeen();
        }

        if (result == DialogResult.OK && form.OpenBoardRequested)
        {
            ShowBoard();
        }
    }

    private void BeginInvoke(Action action)
    {
        if (invoker.IsDisposed)
        {
            return;
        }

        invoker.BeginInvoke(action);
    }

    private async Task AutoCaptureCopyAsync(PendingShortcut pending)
    {
        await Task.Delay((int)holdDelay.TotalMilliseconds + 25);
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
        var text = pending.ObservedClipboardText
            ?? await WaitForClipboardTextAsync(pending.ClipboardSequenceNumber, 75);
        if (text is null || pending.Cancelled)
        {
            return;
        }

        var note = store.AddNote(bucket, text, "copy");
        PushUndo(
            pending.ShiftLane,
            $"Undid capture to {bucket.Name}.",
            () => store.DeleteNote(bucket, note.Id));
        ShowInfo($"Captured to {DestinationLabel(project, bucket)}.");
    }

    private async Task HandleHoldAsync(ChordlEventContext context, IntPtr targetWindow)
    {
        var pending = CancelPending(context.KeyCode, context.ShiftLane);
        switch (context.KeyCode)
        {
            case VK_B:
                ShowBoard(context.ShiftLane, targetWindow);
                break;
            case VK_C:
                await HandleCopyHoldAsync(context.ShiftLane, targetWindow, pending?.ClipboardSequenceNumber ?? context.ClipboardSequenceNumber, pending?.ObservedClipboardText);
                break;
            case VK_P:
                HandlePopToggleHold(context.ShiftLane);
                break;
            case VK_R:
                HandleFifoToggleHold(context.ShiftLane);
                break;
            case VK_X:
                await HandleCutHoldAsync(context.ShiftLane, targetWindow, pending?.ClipboardSequenceNumber ?? context.ClipboardSequenceNumber, pending?.ObservedClipboardText);
                break;
            case VK_V:
                HandlePasteHold(context.ShiftLane, targetWindow);
                break;
            case VK_Z:
                HandleUndoHold(context.ShiftLane);
                break;
        }
    }

    private void HandleFifoToggleHold(bool shifted)
    {
        var bucket = store.GetActiveBucket(shifted);
        if (bucket is null)
        {
            ShowInfo("No active bucket yet.");
            return;
        }

        if (ZetlStateStore.IsFifoBucket(bucket))
        {
            store.SetBucketKind(bucket, "Standard");
            ResetReplayClipboardTracking(shifted);
            ShowInfo($"{bucket.Name} replay is off.");
            return;
        }

        store.SetBucketKind(bucket, "Replay");
        // Turning replay on no longer pre-loads the first item onto the clipboard:
        // that would clobber the user's last copy. Each Ctrl+V borrows the queue
        // item only for its own paste. Start tracking fresh so the first replay
        // press snapshots the real clipboard.
        ResetReplayClipboardTracking(shifted);
        ShowInfo($"{bucket.Name} replay is on.");
    }

    private void HandlePopToggleHold(bool shifted)
    {
        var bucket = store.GetActiveBucket(shifted);
        if (bucket is null)
        {
            ShowInfo("No active bucket yet.");
            return;
        }

        if (ZetlStateStore.IsFifoBucket(bucket))
        {
            // Replay and Pop are exclusive; switch a Replay bucket straight to
            // Pop, mirroring how Ctrl+R switches a Pop bucket to Replay.
            store.SetBucketKind(bucket, "Standard");
            store.SetBucketPopMode(bucket, true);
            ShowInfo($"{bucket.Name} pop is on.");
            return;
        }

        store.ToggleActiveBucketPopMode(shifted);
        ShowInfo($"{bucket.Name} pop is {(bucket.PopMode ? "on" : "off")}.");
    }

    private async Task HandleCopyHoldAsync(bool shifted, IntPtr targetWindow, uint beforeSequence, string? observedText)
    {
        var hadActiveProject = store.GetActiveProject(shifted) is not null;
        store.GetOrCreateDefaultProject(shifted);
        var text = observedText
            ?? await WaitForClipboardTextAsync(beforeSequence, 300)
            ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowBoard(shifted, targetWindow);
            return;
        }

        ShowNoteDialog(
            text,
            store.GetActiveBucket(shifted),
            "copy",
            shifted,
            targetWindow,
            showStartProjectToggle: true,
            startProjectDefault: !hadActiveProject,
            scratchOnlyUntilProjectStarted: !hadActiveProject,
            createNewProjectToggle: hadActiveProject,
            projectToggleText: hadActiveProject ? "New project" : "Start project",
            projectNameDefault: hadActiveProject ? DefaultProjectName(shifted) : null);
    }

    private async Task HandleCutHoldAsync(bool shifted, IntPtr targetWindow, uint beforeSequence, string? observedText)
    {
        var hadActiveProject = store.GetActiveProject(shifted) is not null;
        var project = store.GetOrCreateDefaultProject(shifted);
        var scratch = store.GetScratchBucket(project);
        var preferredBucket = hadActiveProject ? store.GetQuickNoteBucket(project) : scratch;
        var text = observedText
            ?? await WaitForClipboardTextAsync(beforeSequence, 300)
            ?? "";
        ShowNoteDialog(
            text,
            preferredBucket,
            "cut",
            shifted,
            targetWindow,
            showStartProjectToggle: !hadActiveProject,
            startProjectDefault: false,
            scratchOnlyUntilProjectStarted: !hadActiveProject);
    }

    private void HandlePasteHold(bool shifted, IntPtr targetWindow)
    {
        var project = store.GetActiveProject(shifted);
        IReadOnlyList<ZetlBucket>? bucketScope = null;
        if (project is null)
        {
            if (!store.TryGetScratchCompileTarget(out project, out var scratchBucket, shifted)
                || project is null
                || scratchBucket is null)
            {
                ShowInfo("No Zetl notes to compile yet.");
                return;
            }

            bucketScope = [scratchBucket];
        }
        else if (!store.HasCompilableNotes(project))
        {
            ShowInfo("No Zetl notes to compile yet.");
            return;
        }

        ZetlProject compileProject = project;
        var form = new CompileForm(store, compileProject, bucketScope);
        ZetlDialogPlacement.ShowForegroundPopup(
            form,
            onClosed: () =>
            {
                if (form.DialogResult != DialogResult.OK || string.IsNullOrWhiteSpace(form.CompiledText))
                {
                    if (!ZetlDialogPlacement.WasClosedByDeactivate(form))
                    {
                        RestoreForegroundWindow(targetWindow);
                    }

                    return;
                }

                if (form.SaveToBucket)
                {
                    var destinationProject = form.DestinationProject;
                    var destination = store.GetOrCreateBucket(destinationProject, form.DestinationBucketName, setActive: false);
                    var destinationLabel = DestinationLabel(destinationProject, destination);
                    string savedSummary;
                    if (form.Flatten)
                    {
                        var note = store.AddNote(destination, form.CompiledText, "compile");
                        PushUndo(
                            shifted,
                            $"Undid compile to {destination.Name}.",
                            () => store.DeleteNote(destination, note.Id));
                        savedSummary = $"to {destinationLabel}";
                    }
                    else
                    {
                        var notes = store.AddNotes(destination, form.SelectedNoteTexts, "compile");
                        PushUndo(
                            shifted,
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

                    ShowInfo($"Compiled {savedSummary}.");
                    RestoreForegroundWindow(targetWindow);
                    return;
                }

                ClipboardText.Set(form.CompiledText);
                if (form.PasteNow)
                {
                    if (targetWindow != IntPtr.Zero)
                    {
                        SetForegroundWindow(targetWindow);
                    }

                    BeginInvoke(async () =>
                    {
                        await Task.Delay(75);
                        ChordlInput.SendPaste(Log);
                        ShowInfo("Pasted compiled text.");
                    });
                }
                else
                {
                    ShowInfo("Copied compiled text to clipboard.");
                    RestoreForegroundWindow(targetWindow);
                }
            },
            owner: ZetlDialogPlacement.OwnerFromHandle(targetWindow),
            activationWindow: targetWindow);
    }

    private void ShowProjectSetupDialog()
    {
        using var form = new ProjectSetupForm(store.Defaults.ProjectBuckets);
        if (ZetlDialogPlacement.ShowForegroundDialog(form) != DialogResult.OK)
        {
            return;
        }

        store.CreateProject(form.ProjectName, form.BucketNames, form.ActiveBucketName);
        ShowBoard();
    }

    private void ShowNoteDialog(
        string text,
        ZetlBucket? preferredBucket,
        string source,
        bool shifted,
        IntPtr restoreWindow,
        bool showStartProjectToggle = false,
        bool startProjectDefault = true,
        bool scratchOnlyUntilProjectStarted = false,
        bool createNewProjectToggle = false,
        string? projectToggleText = null,
        string? projectNameDefault = null)
    {
        var project = store.GetActiveProject(shifted);
        if (project is null)
        {
            return;
        }

        ZetlProject noteProject = project;
        var form = new NoteCaptureForm(
            store,
            noteProject,
            preferredBucket,
            text,
            showStartProjectToggle,
            startProjectDefault,
            scratchOnlyUntilProjectStarted,
            createNewProjectToggle,
            projectToggleText,
            projectNameDefault);
        ZetlDialogPlacement.ShowForegroundPopup(
            form,
            onClosed: () =>
            {
                try
                {
                    // Clicking off the popup commits the note just like the Save
                    // button does, using whatever toggle state the dialog is in.
                    var committed = form.DialogResult == DialogResult.OK
                        || ZetlDialogPlacement.WasClosedByDeactivate(form);
                    if (!committed || string.IsNullOrWhiteSpace(form.NoteText))
                    {
                        if (showStartProjectToggle && !createNewProjectToggle)
                        {
                            store.ClearActiveProject(shifted);
                        }

                        return;
                    }

                    if (form.CreateNewProject)
                    {
                        var bucketNames = store.Defaults.ProjectBuckets.Count > 0
                            ? store.Defaults.ProjectBuckets
                            : new List<string> { "Inbox", "Scratch" };
                        noteProject = store.CreateProject(form.ProjectName, bucketNames, form.SelectedBucketName, shifted);
                    }
                    else if (form.StartProject)
                    {
                        store.UpdateProjectName(noteProject, form.ProjectName, shifted);
                    }

                    var bucket = form.CreateNewProject
                        ? noteProject.Buckets.FirstOrDefault(bucket =>
                            string.Equals(bucket.Name, form.SelectedBucketName, StringComparison.OrdinalIgnoreCase))
                            ?? noteProject.Buckets.First()
                        : form.SelectedBucket;
                    if (!form.CreateNewProject
                        && form.StartProject
                        && string.Equals(source, "copy", StringComparison.OrdinalIgnoreCase))
                    {
                        store.SetActiveBucket(noteProject, bucket.Id);
                    }

                    if (string.Equals(source, "cut", StringComparison.OrdinalIgnoreCase))
                    {
                        store.SetQuickNoteBucket(noteProject, bucket.Id);
                    }

                    var note = store.AddNote(bucket, form.NoteText, source);
                    PushUndo(
                        shifted,
                        $"Undid save to {bucket.Name}.",
                        () => store.DeleteNote(bucket, note.Id));
                    ClipboardText.Set(form.NoteText);
                    if (showStartProjectToggle && !createNewProjectToggle && !form.StartProject)
                    {
                        store.ClearActiveProject(shifted);
                    }

                    ShowInfo($"Saved to {DestinationLabel(noteProject, bucket)}.");
                }
                finally
                {
                    if (!ZetlDialogPlacement.WasClosedByDeactivate(form))
                    {
                        RestoreForegroundWindow(restoreWindow);
                    }
                }
            },
            owner: ZetlDialogPlacement.OwnerFromHandle(restoreWindow),
            activationWindow: restoreWindow);
    }

    private void ShowBoard(bool shifted = false, IntPtr restoreWindow = default)
    {
        if (boardForm is null || boardForm.IsDisposed || boardForm.ShiftedLane != shifted)
        {
            boardForm?.Dispose();
            boardForm = new BoardForm(store, shifted);
        }

        boardForm.RestoreWindowOnClose = restoreWindow;
        boardForm.AutoHideOnDeactivate = restoreWindow != IntPtr.Zero;
        boardForm.ShowInTaskbar = false;
        boardForm.ShowActiveProject();
        ZetlDialogPlacement.PlaceNearTopSixth(boardForm);
        var owner = ZetlDialogPlacement.OwnerFromHandle(restoreWindow);
        if (!boardForm.Visible && owner is not null)
        {
            boardForm.Show(owner);
        }
        else
        {
            boardForm.Show();
        }

        boardForm.WindowState = FormWindowState.Normal;
        ZetlDialogPlacement.BringToForeground(boardForm, restoreWindow);
    }

    private void HandleUndoHold(bool shifted)
    {
        var undoIndex = undoStack.FindLastIndex(action => action.Shifted == shifted);
        if (undoIndex < 0)
        {
            ShowInfo("Nothing to undo.");
            return;
        }

        var action = undoStack[undoIndex];
        undoStack.RemoveAt(undoIndex);
        try
        {
            action.Undo();
            ShowInfo(action.Message);
        }
        catch (Exception ex)
        {
            Log($"Undo failed: {ex.Message}");
            ShowInfo("Zetl undo failed.");
        }
    }

    private void PushUndo(bool shifted, string message, Action undo)
    {
        undoStack.Add(new ZetlUndoAction(shifted, message, undo));
        if (undoStack.Count > MaxUndoActions)
        {
            undoStack.RemoveRange(0, undoStack.Count - MaxUndoActions);
        }
    }

    private PendingShortcut? CancelPending(int keyCode, bool shifted)
    {
        lock (pendingShortcuts)
        {
            var key = PendingKey(keyCode, shifted);
            if (!pendingShortcuts.TryGetValue(key, out var pending))
            {
                return null;
            }

            pending.Cancelled = true;
            pendingShortcuts.Remove(key);
            return pending;
        }
    }

    private async Task ObserveClipboardChangeAsync(PendingShortcut pending)
    {
        pending.ObservedClipboardText = await WaitForClipboardTextAsync(pending.ClipboardSequenceNumber, 500);
    }

    private async Task<string?> WaitForClipboardTextAsync(uint beforeSequence, int timeoutMs)
    {
        var started = Environment.TickCount64;
        do
        {
            if (GetClipboardSequenceNumber() != beforeSequence)
            {
                var text = ClipboardText.TryGet();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }

            await Task.Delay(20);
        }
        while (Environment.TickCount64 - started < timeoutMs);

        return null;
    }

    private void ShowInfo(string message)
    {
        toastService.Show(message);
        EnqueueLogNote(message);
    }

    private void EnqueueLogNote(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (pendingLogNotes)
        {
            pendingLogNotes.Add(line);
        }
    }

    private void FlushLogNotes()
    {
        List<string> batch;
        lock (pendingLogNotes)
        {
            if (pendingLogNotes.Count == 0)
            {
                return;
            }

            batch = new List<string>(pendingLogNotes);
            pendingLogNotes.Clear();
        }

        try
        {
            store.AppendLogNotes(batch, LogRetentionDays, LogMaxNotesPerDay);
        }
        catch (Exception ex)
        {
            // Keep flush failures off the toast path (that would re-enqueue and
            // loop); the in-memory trace is enough to notice them.
            Log($"Log note flush failed: {ex.Message}");
        }
    }

    private static void RestoreForegroundWindow(IntPtr window)
    {
        if (window != IntPtr.Zero)
        {
            SetForegroundWindow(window);
        }
    }

    private static (int KeyCode, bool Shifted) PendingKey(int keyCode, bool shifted)
    {
        return (keyCode, shifted);
    }

    private static string DefaultProjectName(bool shifted = false)
    {
        var name = DateTime.Now.ToString("yyyy-MM-dd");
        return shifted ? $"{name} Shift" : name;
    }

    private sealed class PendingShortcut(int keyCode, bool shiftLane, uint clipboardSequenceNumber)
    {
        public int KeyCode { get; } = keyCode;
        public bool ShiftLane { get; } = shiftLane;
        public uint ClipboardSequenceNumber { get; } = clipboardSequenceNumber;
        public string? ObservedClipboardText { get; set; }
        public bool Cancelled { get; set; }
    }

    private sealed record ZetlUndoAction(bool Shifted, string Message, Action Undo);
}
