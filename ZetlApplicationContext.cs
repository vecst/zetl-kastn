using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Program;

namespace ZETL;

internal sealed class ZetlApplicationContext :
    ApplicationContext,
    IZetlDispatcher,
    IZetlNotificationSink
{
    private const int MaxUndoActions = 100;
    private const int LogFlushIntervalMs = 5000;
    private const int LogRetentionDays = 14;
    private const int LogMaxNotesPerDay = 2000;
    private readonly ZetlStateStore store;
    private readonly ZetlAppSettingsStore appSettings;
    private readonly NotifyIcon trayIcon;
    private readonly Control invoker = new();
    private readonly ZetlToastService toastService = new();
    private readonly ZetlUndoStack undoStack = new(MaxUndoActions);
    private readonly List<string> logLines = new();
    // Activity-log lines are buffered so persisting them never sits on the
    // per-keystroke path; a timer drains this into the "Zetl Logs" project.
    private readonly ZetlActivityLogBuffer activityLogBuffer = new();
    private System.Windows.Forms.Timer? logFlushTimer;
    private BoardForm? boardForm;

    private readonly TimeSpan holdDelay;
    private readonly IKeyboardBackend keyboard;
    private readonly IClipboard clipboard;
    private readonly ZetlShortcutCoordinator shortcutCoordinator;
    private readonly ICaptureOriginProvider captureOriginProvider = new WindowsCaptureOriginProvider();

    public ZetlApplicationContext(
        TimeSpan holdDelay,
        IKeyboardBackend keyboard,
        IClipboard clipboard,
        string? dataDirectory = null)
    {
        this.holdDelay = holdDelay;
        this.keyboard = keyboard;
        this.clipboard = clipboard;
        store = dataDirectory is null
            ? new ZetlStateStore()
            : new ZetlStateStore(Path.Combine(dataDirectory, "state.json"));
        appSettings = new ZetlAppSettingsStore(dataDirectory is null
            ? null
            : Path.Combine(dataDirectory, "settings.json"));
        shortcutCoordinator = new ZetlShortcutCoordinator(
            store,
            keyboard,
            clipboard,
            this,
            new SystemZetlDelay(),
            this,
            undoStack,
            () => appSettings.Settings.AutoCaptureOnCopy,
            () => appSettings.Settings.QuickNoteToClipboard,
            Log,
            holdDelay);
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
        var captureOrigin = context.KeyCode is VK_C or VK_X
            ? captureOriginProvider.Capture(appSettings.Settings.CaptureOriginDetail)
            : null;
        ZetlAsync.RunLogged(
            () => shortcutCoordinator.OnPhysicalShortcutPassedThroughAsync(
                context,
                captureOrigin),
            "physical shortcut pass-through",
            Log);
    }

    private void ApplyAppSettings()
    {
        var settings = appSettings.Settings;
        toastService.DisplayMilliseconds = settings.ToastDisplayMs;
        ZetlRuntimeSettings.ApplyTo(store, settings);
    }

    public bool OnTapDispatched(ChordlEventContext context)
    {
        return shortcutCoordinator.OnTapDispatched(context);
    }

    public void OnHoldDetected(ChordlEventContext context)
    {
        var pending = shortcutCoordinator.ClaimPendingForHold(context);
        var targetWindow = GetForegroundWindow();
        BeginInvoke(() => ZetlAsync.RunLogged(
            () => HandleHoldAsync(context, targetWindow, pending),
            "hold action",
            Log));
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
        settings.QuickNoteToClipboard = form.QuickNoteToClipboard;
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

    private async Task HandleHoldAsync(
        ChordlEventContext context,
        IntPtr targetWindow,
        ZetlPendingShortcut? pending)
    {
        var request = await shortcutCoordinator.HandleClaimedHoldAsync(
            context,
            pending);
        switch (request)
        {
            case ZetlBoardRequest board:
                ShowBoard(board.Shifted, targetWindow);
                break;
            case ZetlNoteCaptureRequest note:
                ShowNoteDialog(note, targetWindow);
                break;
            case ZetlCompileRequest compile:
                ShowCompileDialog(compile, targetWindow);
                break;
        }
    }

    private void ShowCompileDialog(ZetlCompileRequest request, IntPtr targetWindow)
    {
        var form = new CompileForm(store, request.Project, request.BucketScope);
        ZetlDialogPlacement.ShowForegroundPopup(
            form,
            onClosed: () =>
            {
                var result = new ZetlCompileResult(
                    form.DialogResult == DialogResult.OK,
                    form.CompiledText,
                    form.SaveToBucket,
                    form.DestinationProject,
                    form.DestinationBucketName,
                    form.Flatten,
                    form.SelectedNoteTexts,
                    form.PasteNow);
                var outcome = shortcutCoordinator.CompleteCompile(request, result);
                if (outcome == ZetlCompileOutcome.None)
                {
                    if (!ZetlDialogPlacement.WasClosedByDeactivate(form))
                    {
                        RestoreForegroundWindow(targetWindow);
                    }

                    return;
                }

                if (outcome == ZetlCompileOutcome.RestoreTarget)
                {
                    RestoreForegroundWindow(targetWindow);
                    return;
                }

                if (targetWindow != IntPtr.Zero)
                {
                    SetForegroundWindow(targetWindow);
                }

                ZetlAsync.RunLogged(shortcutCoordinator.PasteCompiledTextAsync, "paste compiled text", Log);
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

    private void ShowNoteDialog(ZetlNoteCaptureRequest request, IntPtr restoreWindow)
    {
        var form = new NoteCaptureForm(
            store,
            request.Project,
            request.PreferredBucket,
            request.Text,
            request.ShowStartProjectToggle,
            request.StartProjectDefault,
            request.ScratchOnlyUntilProjectStarted,
            request.CreateNewProjectToggle,
            request.ProjectToggleText,
            request.ProjectNameDefault);
        ZetlDialogPlacement.ShowForegroundPopup(
            form,
            onClosed: () =>
            {
                try
                {
                    var committed = form.DialogResult == DialogResult.OK
                        || ZetlDialogPlacement.WasClosedByDeactivate(form);
                    shortcutCoordinator.CompleteNoteCapture(
                        request,
                        new ZetlNoteCaptureResult(
                            committed,
                            form.NoteText,
                            form.StartProject,
                            form.CreateNewProject,
                            form.ProjectName,
                            form.SelectedBucketName,
                            form.SelectedBucket));
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

    private void ShowInfo(string message)
    {
        toastService.Show(message);
        EnqueueLogNote(message);
    }

    private void EnqueueLogNote(string message)
    {
        activityLogBuffer.Enqueue(message);
    }

    private void FlushLogNotes()
    {
        var batch = activityLogBuffer.Drain();
        if (batch.Count == 0)
        {
            return;
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

    void IZetlDispatcher.Post(Action action)
    {
        BeginInvoke(action);
    }

    void IZetlNotificationSink.Show(string message)
    {
        ShowInfo(message);
    }
}
