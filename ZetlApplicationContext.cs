using System.Runtime.InteropServices;
using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Program;

namespace ZETL;

internal sealed class ZetlApplicationContext : ApplicationContext
{
    private const int MaxUndoActions = 100;
    private readonly ZetlStateStore store = new();
    private readonly ZetlAppSettingsStore appSettings = new();
    private readonly NotifyIcon trayIcon;
    private readonly Control invoker = new();
    private readonly ZetlToastService toastService = new();
    private readonly Dictionary<(int KeyCode, bool Shifted), PendingShortcut> pendingShortcuts = new();
    private readonly List<ZetlUndoAction> undoStack = new();
    private readonly List<string> logLines = new();
    private BoardForm? boardForm;

    private readonly TimeSpan holdDelay;

    public ZetlApplicationContext(TimeSpan holdDelay)
    {
        this.holdDelay = holdDelay;
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

        if (context.KeyCode == VK_C)
        {
            BeginInvoke(async () => await AutoCaptureCopyAsync(pending));
        }
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
                var project = store.GetActiveProject(context.ShiftLane);
                var noteId = fifoNote.Id;
                var noteText = fifoNote.Text;
                var bucketName = activeBucket.Name;
                BeginInvoke(() =>
                {
                    SetClipboardText(noteText);
                    if (!ChordlInput.SendPaste(Log))
                    {
                        ShowInfo($"Paste failed; {bucketName} item kept.");
                        return;
                    }

                    ZetlBucket? reviewBucket = null;
                    ZetlNote? consumedNote = null;
                    ZetlNote? reviewNote = null;
                    var consumed = project is not null
                        ? store.TryConsumeFifoNoteToReview(project, activeBucket, noteId, out reviewBucket, out consumedNote, out reviewNote)
                        : store.TryConsumeFifoNote(activeBucket, noteId, out consumedNote);
                    if (consumed && reviewBucket is not null)
                    {
                        Log($"Archived FIFO paste from {bucketName} to {reviewBucket.Name}.");
                    }

                    if (consumed && consumedNote is not null)
                    {
                        var undoReviewBucket = reviewBucket;
                        var undoReviewNoteId = reviewNote?.Id;
                        PushUndo(
                            context.ShiftLane,
                            $"Restored FIFO item to {bucketName}.",
                            () => store.RestoreFifoConsumedNote(activeBucket, consumedNote, undoReviewBucket, undoReviewNoteId));
                    }

                    if (!store.TryPeekNextFifoNote(activeBucket, out _))
                    {
                        store.SetBucketKind(activeBucket, "Standard");
                        ShowInfo($"{bucketName} FIFO complete.");
                        return;
                    }

                    ShowInfo($"Pasted next item from {bucketName}.");
                });
                return true;
            }

            BeginInvoke(() =>
            {
                store.SetBucketKind(activeBucket, "Standard");
                ChordlInput.SendPaste(Log);
                ShowInfo($"{activeBucket.Name} FIFO complete.");
            });
            return true;
        }

        BeginInvoke(async () =>
        {
            await Task.Delay(75);
            var text = TryGetClipboardText();
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

    public void OnHoldDetected(ChordlEventContext context)
    {
        var targetWindow = GetForegroundWindow();
        BeginInvoke(async () => await HandleHoldAsync(context, targetWindow));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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
                ShowInfo("FIFO buckets cannot use pop mode.");
                return;
            }

            store.ToggleActiveBucketPopMode();
            var bucket = store.ActiveBucket;
            ShowInfo(bucket is null
                ? "No active bucket yet."
                : $"{bucket.Name} pop mode is {(bucket.PopMode ? "on" : "off")}.");
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
        return menu;
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
        ShowInfo($"Captured to {bucket.Name}.");
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
            case VK_X:
                await HandleCutHoldAsync(context.ShiftLane, targetWindow, pending?.ClipboardSequenceNumber ?? context.ClipboardSequenceNumber, pending?.ObservedClipboardText);
                break;
            case VK_V:
                await HandlePasteHoldAsync(context.ShiftLane, targetWindow);
                break;
            case VK_Z:
                HandleUndoHold(context.ShiftLane);
                break;
        }
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
            showStartProjectToggle: !hadActiveProject,
            startProjectDefault: true,
            scratchOnlyUntilProjectStarted: !hadActiveProject);
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

    private async Task HandlePasteHoldAsync(bool shifted, IntPtr targetWindow)
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

        using var form = new CompileForm(store, project, bucketScope);
        var result = ZetlDialogPlacement.ShowForegroundDialog(
            form,
            ZetlDialogPlacement.OwnerFromHandle(targetWindow),
            closeOnDeactivate: true,
            activationWindow: targetWindow);
        if (result != DialogResult.OK || string.IsNullOrWhiteSpace(form.CompiledText))
        {
            if (!ZetlDialogPlacement.WasClosedByDeactivate(form))
            {
                RestoreForegroundWindow(targetWindow);
            }

            return;
        }

        if (form.SaveToBucket)
        {
            var destination = store.GetOrCreateBucket(project, form.DestinationBucketName);
            var note = store.AddNote(destination, form.CompiledText, "compile");
            PushUndo(
                shifted,
                $"Undid compile to {destination.Name}.",
                () => store.DeleteNote(destination, note.Id));
            ShowInfo($"Compiled to {destination.Name}.");
            RestoreForegroundWindow(targetWindow);
            return;
        }

        Clipboard.SetText(form.CompiledText);
        if (form.PasteNow)
        {
            if (targetWindow != IntPtr.Zero)
            {
                SetForegroundWindow(targetWindow);
            }

            await Task.Delay(75);
            ChordlInput.SendPaste(Log);
            ShowInfo("Pasted compiled text.");
        }
        else
        {
            ShowInfo("Copied compiled text to clipboard.");
            RestoreForegroundWindow(targetWindow);
        }
    }

    private void ShowProjectSetupDialog()
    {
        using var form = new ProjectSetupForm();
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
        bool scratchOnlyUntilProjectStarted = false)
    {
        var project = store.GetActiveProject(shifted);
        if (project is null)
        {
            return;
        }

        using var form = new NoteCaptureForm(
            store,
            project,
            preferredBucket,
            text,
            showStartProjectToggle,
            startProjectDefault,
            scratchOnlyUntilProjectStarted);
        var result = ZetlDialogPlacement.ShowForegroundDialog(
            form,
            ZetlDialogPlacement.OwnerFromHandle(restoreWindow),
            closeOnDeactivate: true,
            activationWindow: restoreWindow);
        try
        {
            if (result != DialogResult.OK || string.IsNullOrWhiteSpace(form.NoteText))
            {
                if (showStartProjectToggle)
                {
                    store.ClearActiveProject(shifted);
                }

                return;
            }

            if (form.StartProject)
            {
                store.UpdateProjectName(project, form.ProjectName, shifted);
            }

            var bucket = form.SelectedBucket;
            if (form.StartProject && string.Equals(source, "copy", StringComparison.OrdinalIgnoreCase))
            {
                store.SetActiveBucket(project, bucket.Id);
            }

            if (string.Equals(source, "cut", StringComparison.OrdinalIgnoreCase))
            {
                store.SetQuickNoteBucket(project, bucket.Id);
            }

            var note = store.AddNote(bucket, form.NoteText, source);
            PushUndo(
                shifted,
                $"Undid save to {bucket.Name}.",
                () => store.DeleteNote(bucket, note.Id));
            SetClipboardText(form.NoteText);
            if (showStartProjectToggle && !form.StartProject)
            {
                store.ClearActiveProject(shifted);
            }

            ShowInfo($"Saved to {bucket.Name}.");
        }
        finally
        {
            if (!ZetlDialogPlacement.WasClosedByDeactivate(form))
            {
                RestoreForegroundWindow(restoreWindow);
            }
        }
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
                var text = TryGetClipboardText();
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

    private static string? TryGetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : null;
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (ThreadStateException)
        {
            return null;
        }
    }

    private static void SetClipboardText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
        }
        catch (ThreadStateException)
        {
        }
    }

    private void ShowInfo(string message)
    {
        toastService.Show(message);
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
