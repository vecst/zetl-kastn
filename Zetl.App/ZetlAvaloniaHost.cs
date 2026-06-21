using System.Reflection;
using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Chordl;
using ZETL.Contracts;

namespace ZETL;

internal sealed class ZetlAvaloniaHost : IZetlDispatcher, IDisposable
{
    private const int MaxUndoActions = 100;
    private const int LogFlushIntervalMs = 5000;
    private const int LogRetentionDays = 14;
    private const int LogMaxNotesPerDay = 2000;

    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly ZetlStateStore store;
    private readonly ZetlProjectService projectService;
    private readonly ZetlIpcServer ipcServer;
    private readonly ZetlAppSettingsStore settingsStore;
    private readonly ZetlThemeStore themeStore;
    private readonly ZetlTemplateStore templateStore;
    private readonly ZetlActivityLogBuffer activityLog = new();
    private readonly AvaloniaNotificationService notifications;
    private readonly ZetlUndoStack undoStack = new(MaxUndoActions);
    private readonly DispatcherTimer logFlushTimer;
    private readonly IKeyboardBackend keyboard;
    private readonly IClipboard clipboard;
    private readonly IImageUrlResolver imageUrlResolver;
    private readonly ZetlShortcutCoordinator coordinator;
    private readonly ChordlProcessor? processor;
    private readonly TrayIcon trayIcon;
    private readonly ZetlThemeManager themeManager;
    private readonly ICaptureOriginProvider captureOriginProvider;
    private readonly Dictionary<bool, BoardWindow> boards = [];
    private readonly object shortcutTargetsGate = new();
    private readonly Dictionary<int, object?> shortcutTargets = [];
    private readonly string diagnosticLogPath;
    private readonly string? kastnPath;
    private readonly string? ipcPipeName;
    private readonly BlockingCollection<string> diagnosticLines = [];
    private readonly Thread diagnosticThread;
    private readonly List<IClickAwayDismissable> clickAwayPopups = [];
    private readonly ZetlClickAwayWatcher clickAwayWatcher;
    private DateTime lastTrayClickUtc = DateTime.MinValue;
    private bool disposed;

    public ZetlAvaloniaHost(
        Application application,
        IClassicDesktopStyleApplicationLifetime desktop,
        string? dataDirectory = null)
    {
        this.desktop = desktop;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        store = dataDirectory is null
            ? new ZetlStateStore(log: Log)
            : new ZetlStateStore(Path.Combine(dataDirectory, "state.json"), log: Log);
        projectService = new ZetlProjectService(store, log: Log);
        var pipeName = Program.StartupArgs
            .FirstOrDefault(arg => arg.StartsWith(
                "--ipc-pipe=",
                StringComparison.OrdinalIgnoreCase))
            ?["--ipc-pipe=".Length..];
        ipcPipeName = pipeName;
        ipcServer = new ZetlIpcServer(projectService, pipeName, Log);
        kastnPath = Program.StartupArgs
            .FirstOrDefault(arg => arg.StartsWith(
                "--kastn-path=",
                StringComparison.OrdinalIgnoreCase))
            ?["--kastn-path=".Length..];
        settingsStore = new ZetlAppSettingsStore(
            dataDirectory is null
                ? null
                : Path.Combine(dataDirectory, "settings.json"),
            Log);
        themeStore = new ZetlThemeStore(dataDirectory is null
            ? null
            : Path.Combine(dataDirectory, "themes"));
        templateStore = new ZetlTemplateStore(
            dataDirectory is null ? null : Path.Combine(dataDirectory, "templates"),
            Log);
        diagnosticLogPath = Path.Combine(
            dataDirectory
                ?? Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ApplicationData),
                    "Zetl"),
            "diagnostics.log");
        diagnosticThread = new Thread(ProcessDiagnosticLines)
        {
            IsBackground = true,
            Name = "Zetl diagnostics"
        };
        diagnosticThread.Start();
        clickAwayWatcher = new ZetlClickAwayWatcher(OnClickOutsideApp);
        captureOriginProvider = new WindowsCaptureOriginProvider();

        themeManager = new ZetlThemeManager(application, settingsStore);
        themeManager.Apply(
            themeStore.Resolve(settingsStore.Settings.ThemeId),
            settingsStore.Settings.ThemeVariant,
            persist: false);

        notifications = new AvaloniaNotificationService(activityLog);
        keyboard = ZetlPlatformServices.CreateKeyboard(Log);
        clipboard = ZetlPlatformServices.CreateClipboard(Log);
        imageUrlResolver = new ZetlImageUrlResolver(Log);
        var config = LoadChordlConfiguration(out var configMessage);
        coordinator = new ZetlShortcutCoordinator(
            store,
            keyboard,
            clipboard,
            this,
            new SystemZetlDelay(),
            notifications,
            undoStack,
            () => settingsStore.Settings.AutoCaptureOnCopy,
            () => settingsStore.Settings.QuickNoteToClipboard,
            Log,
            config.HoldDelay,
            imageUrlResolver);

        ApplySettings();
        store.ConsolidateDefaultProject();
        store.ConsolidateDefaultProject(shifted: true);
        store.ClearActiveProject();
        store.ClearActiveProject(shifted: true);

        processor = new ChordlProcessor(
            config.Actions,
            config.ConfiguredKeyCodes,
            config.RepeatSuppressionDelay,
            config.HoldDelay,
            DispatchOriginalAction,
            OnPhysicalShortcutPassedThrough,
            coordinator.OnTapDispatched,
            OnHoldDetected,
            Log,
            clipboard.GetChangeToken);

        trayIcon = CreateTrayIcon();
        // Reflect Zetl's state in the tray icon. store.Changed covers active
        // project, bucket kind, and pop toggles; UpdateTrayIcon no-ops when the
        // effective state is unchanged, so this stays cheap despite firing often.
        store.Changed += (_, _) => Dispatcher.UIThread.Post(UpdateTrayIcon);
        // Announce projects created through the project service — notably a template
        // used in Kastn, which creates and activates the project in Zetl over IPC —
        // so the user sees Zetl is now armed with it (ready to capture or replay).
        projectService.ProjectChanged += OnProjectServiceProjectChanged;
        logFlushTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(LogFlushIntervalMs)
        };
        logFlushTimer.Tick += (_, _) => FlushLogNotes();
        logFlushTimer.Start();
        ipcServer.Start();

        Log(configMessage);
        if (!keyboard.Start(processor.HandleKeyEvent))
        {
            notifications.Show(OperatingSystem.IsWindows()
                ? "Failed to install the global keyboard hook."
                : "Zetl started without global shortcuts; the Linux input backend is not installed yet.");
        }
        else
        {
            Log($"Loaded {config.Actions.Count} Chordl definitions.");
        }

        Dispatcher.UIThread.Post(ShowFirstRunIfNeeded);
    }

    public void Post(Action action)
    {
        Dispatcher.UIThread.Post(action);
    }

    public void Log(string message)
    {
        WriteDiagnosticLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    public void OpenBoard()
    {
        ShowBoard(shifted: false);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ipcServer.Dispose();
        clickAwayWatcher.Stop();
        logFlushTimer.Stop();
        FlushLogNotes();
        foreach (var board in boards.Values.ToList())
        {
            board.Close();
        }

        trayIcon.IsVisible = false;
        trayIcon.Dispose();
        notifications.Dispose();
        processor?.Dispose();
        keyboard.Dispose();
        (clipboard as IDisposable)?.Dispose();
        diagnosticLines.CompleteAdding();
        diagnosticThread.Join(TimeSpan.FromSeconds(1));
    }

    // Fired by the click-away watcher (UI thread) when the user clicks or
    // moves the foreground outside Zetl's windows. Dismisses any click-away
    // popups, covering cases the OS Deactivated event misses (e.g. clicking
    // the bare desktop, which does not move the foreground).
    private void OnClickOutsideApp()
    {
        foreach (var popup in clickAwayPopups.ToList())
        {
            popup.DismissFromClickAway();
        }
    }

    private void RegisterClickAwayPopup(Window window)
    {
        if (window is not IClickAwayDismissable dismissable)
        {
            return;
        }

        if (!clickAwayPopups.Contains(dismissable))
        {
            clickAwayPopups.Add(dismissable);
            clickAwayWatcher.Start();
        }

        window.Closed += (_, _) =>
        {
            clickAwayPopups.Remove(dismissable);
            if (clickAwayPopups.Count == 0)
            {
                clickAwayWatcher.Stop();
            }
        };
    }

    private TrayIcon CreateTrayIcon()
    {
        var menu = new NativeMenu();
        menu.Items.Add(Item("Open Board", () => ShowBoard(shifted: false)));
        menu.Items.Add(Item("Open Shift Board", () => ShowBoard(shifted: true)));
        menu.Items.Add(Item("New Project", () => _ = ShowProjectSetupAsync()));
        menu.Items.Add(Item("How Zetl Works", () => _ = ShowFirstRunGuideAsync(markSeen: false)));
        menu.Items.Add(Item("Notification History", notifications.ShowHistory));
        menu.Items.Add(Item("Clear Notification History", notifications.ClearHistory));
        menu.Items.Add(Item("Toggle Active Bucket Pop Mode", TogglePopMode));
        menu.Items.Add(Item("Settings", () => _ = ShowSettingsAsync()));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Quit", () => desktop.Shutdown()));

        currentTrayState = ComputeTrayState();
        var icon = new TrayIcon
        {
            Icon = TrayIconForState(currentTrayState),
            ToolTipText = TrayTooltip(currentTrayState),
            Menu = menu,
            IsVisible = true
        };
        icon.Clicked += (_, _) => OnTrayIconClicked();
        return icon;
    }

    // Tray icon state, ordered so the most attention-worthy mode wins when the
    // two lanes differ (Replay > Pop > Active > Idle).
    private enum TrayIconState
    {
        Idle = 0,
        Active = 1,
        Pop = 2,
        Replay = 3
    }

    // White Z over a left-to-right gradient. Idle is a muted grey-periwinkle
    // (solid). Active is solid green. Replay/Pop fade green into the status hue
    // (purple for replay, orange for pop), so "active" reads on the left while
    // the mode shows on the right.
    private const uint TrayIdleColor = 0x7B779C;
    private const uint TrayActiveColor = 0x2FA565;
    private const uint TrayReplayColor = 0x9B4FD6;
    private const uint TrayPopColor = 0xEE8A2D;

    private readonly Dictionary<TrayIconState, WindowIcon> trayIcons = [];
    private TrayIconState currentTrayState;

    private TrayIconState ComputeTrayState()
    {
        var normal = (int)LaneTrayState(shifted: false);
        var shift = (int)LaneTrayState(shifted: true);
        return (TrayIconState)Math.Max(normal, shift);
    }

    private TrayIconState LaneTrayState(bool shifted)
    {
        if (store.GetActiveProject(shifted) is null)
        {
            return TrayIconState.Idle;
        }

        var bucket = store.GetActiveBucket(shifted);
        if (bucket is null)
        {
            return TrayIconState.Active;
        }

        if (ZetlStateStore.IsFifoBucket(bucket))
        {
            return TrayIconState.Replay;
        }

        return bucket.PopMode ? TrayIconState.Pop : TrayIconState.Active;
    }

    private void UpdateTrayIcon()
    {
        if (disposed)
        {
            return;
        }

        var state = ComputeTrayState();
        if (state == currentTrayState)
        {
            return;
        }

        currentTrayState = state;
        trayIcon.Icon = TrayIconForState(state);
        trayIcon.ToolTipText = TrayTooltip(state);
    }

    private WindowIcon TrayIconForState(TrayIconState state)
    {
        if (!trayIcons.TryGetValue(state, out var icon))
        {
            icon = state switch
            {
                TrayIconState.Active => CreateTrayWindowIcon(TrayActiveColor, TrayActiveColor),
                TrayIconState.Replay => CreateTrayWindowIcon(TrayActiveColor, TrayReplayColor),
                TrayIconState.Pop => CreateTrayWindowIcon(TrayActiveColor, TrayPopColor),
                _ => CreateTrayWindowIcon(TrayIdleColor, TrayIdleColor)
            };
            trayIcons[state] = icon;
        }

        return icon;
    }

    private static string TrayTooltip(TrayIconState state)
    {
        return state switch
        {
            TrayIconState.Active => "Zetl — project active",
            TrayIconState.Replay => "Zetl — replay mode",
            TrayIconState.Pop => "Zetl — pop mode",
            _ => "Zetl"
        };
    }

    // Avalonia's TrayIcon exposes only a single Clicked event, so we time two
    // clicks ourselves: a lone click does nothing (the icon is easy to hit by
    // accident), and a double-click opens the Board. Right-click still shows the
    // menu, which has its own Open Board item.
    private void OnTrayIconClicked()
    {
        var now = DateTime.UtcNow;
        var doubleClickMs = OperatingSystem.IsWindows()
            ? (int)Win32Interop.GetDoubleClickTime()
            : 500;
        if ((now - lastTrayClickUtc).TotalMilliseconds <= doubleClickMs)
        {
            // Consume the pair so a triple-click isn't read as two double-clicks.
            lastTrayClickUtc = DateTime.MinValue;
            ShowBoard(shifted: false);
            return;
        }

        lastTrayClickUtc = now;
    }

    private static NativeMenuItem Item(string label, Action action)
    {
        var item = new NativeMenuItem(label);
        item.Click += (_, _) => action();
        return item;
    }

    private void OnHoldDetected(ChordlEventContext context)
    {
        var pending = coordinator.ClaimPendingForHold(context);
        var target = TakeShortcutTarget(context.KeyCode)
            ?? ZetlForegroundService.CaptureTarget();
        Log($"{context.Name} hold target: {ZetlForegroundService.DescribeTarget(target)}.");
        Dispatcher.UIThread.Post(() => ZetlAsync.RunLogged(
            () => HandleHoldAsync(context, target, pending),
            "hold action",
            Log));
    }

    private void OnPhysicalShortcutPassedThrough(ChordlEventContext context)
    {
        if (context.KeyCode is ChordlKeys.VK_C or ChordlKeys.VK_X)
        {
            var captureOrigin = captureOriginProvider.Capture(
                settingsStore.Settings.CaptureOriginDetail);
            var target = ZetlForegroundService.CaptureTarget();
            lock (shortcutTargetsGate)
            {
                shortcutTargets[context.KeyCode] = target;
            }

            Log($"{context.Name} keydown target: {ZetlForegroundService.DescribeTarget(target)}.");
            ZetlAsync.RunLogged(
                () => coordinator.OnPhysicalShortcutPassedThroughAsync(
                    context,
                    captureOrigin),
                "physical shortcut pass-through",
                Log);
            return;
        }

        ZetlAsync.RunLogged(
            () => coordinator.OnPhysicalShortcutPassedThroughAsync(context),
            "physical shortcut pass-through",
            Log);
    }

    private object? TakeShortcutTarget(int keyCode)
    {
        lock (shortcutTargetsGate)
        {
            if (!shortcutTargets.Remove(keyCode, out var target))
            {
                return null;
            }

            return target;
        }
    }

    private async Task HandleHoldAsync(
        ChordlEventContext context,
        object? target,
        ZetlPendingShortcut? pending)
    {
        var request = await coordinator.HandleClaimedHoldAsync(
            context,
            pending);
        switch (request)
        {
            case ZetlBoardRequest board:
                ShowBoard(board.Shifted, target);
                break;
            case ZetlNoteCaptureRequest note:
                ShowNoteCapture(note, target);
                break;
            case ZetlCompileRequest compile:
                ShowCompile(compile, target);
                break;
            case ZetlTemplatePickerRequest picker:
                ShowTemplatePicker(picker, target);
                break;
        }
    }

    // Zetl's quick template access: a held Ctrl+T (or a held Ctrl+V with no active
    // project and nothing to compile) lets the user start a fresh project from a
    // consumable template and immediately replay it. When no consumable templates
    // exist, fall back to the original behavior/message.
    private void ShowTemplatePicker(ZetlTemplatePickerRequest request, object? target)
    {
        var templates = templateStore.LoadAll();
        if (templates.Count == 0)
        {
            // No templates at all (built-ins always ship some, so this is rare):
            // keep the original message for the compile fallback path.
            notifications.Show(request.FromCompileFallback
                ? "No Zetl notes to compile yet."
                : "No templates yet. Create one in Kastn.");
            return;
        }

        // A held Ctrl+V fallback defaults to consumable (start replaying); a held
        // Ctrl+T defaults to capture (start a project). Either way the switcher
        // exposes both kinds.
        var window = new TemplatePickerWindow(templates, initialConsumable: request.FromCompileFallback)
        {
            ShowInTaskbar = false,
            DismissOnDeactivate = target is not null
        };
        ConfigureAndShowPopup(window, target, "Template picker", () =>
        {
            if (window.SelectedTemplate is { } template)
            {
                CreateProjectFromTemplate(
                    template,
                    $"{template.Name} {DateTime.Now:yyyy-MM-dd HH:mm}",
                    request.Shifted);
            }
        });
    }

    private void ShowBoard(bool shifted, object? target = null)
    {
        if (!boards.TryGetValue(shifted, out var board))
        {
            board = new BoardWindow(
                store,
                shifted,
                templateStore,
                OpenInKastn,
                CreateProjectFromTemplate)
            {
                ShowInTaskbar = false
            };
            var lane = shifted;
            // Restore on Closing (before the window vanishes) so focus hands off
            // directly, without Windows flashing whatever it picks first.
            board.Closing += (_, _) =>
            {
                if (!board.ClosedByDeactivate)
                {
                    ZetlForegroundService.RestoreTarget(board.ForegroundTarget, Log);
                }
            };
            board.Closed += (_, _) => boards.Remove(lane);
            RegisterClickAwayPopup(board);
            boards[lane] = board;
        }

        board.ForegroundTarget = target;
        board.DismissOnDeactivate = target is not null;
        board.ShowActiveProject();
        PositionAndActivate(board, target);
    }

    private void OpenInKastn(string projectId)
    {
        try
        {
            ZetlKastnLauncher.Launch(projectId, kastnPath, ipcPipeName);
            Log($"Opened project {projectId} in Kastn.");
        }
        catch (Exception ex)
        {
            Log($"Open in Kastn failed ({ex.GetType().Name}): {ex.Message}");
            notifications.Show($"Could not open Kastn: {ex.Message}");
        }
    }

    private void ShowNoteCapture(
        ZetlNoteCaptureRequest request,
        object? target)
    {
        var window = new NoteCaptureWindow(
            store,
            request.Project,
            request.PreferredBucket,
            request.Text,
            noActiveProject: request.ShowStartProjectToggle,
            activateByDefault: request.StartProjectDefault,
            image: request.Image)
        {
            ShowInTaskbar = false,
            DismissOnDeactivate = target is not null
        };
        ConfigureAndShowPopup(window, target, "Note", () =>
        {
            Log(
                "Note popup closed: "
                + $"saved={window.Saved}, "
                + $"deactivate={window.ClosedByDeactivate}.");
            var outcome = coordinator.CompleteNoteCapture(
                request,
                new ZetlNoteCaptureResult(
                    window.Saved,
                    window.NoteText,
                    window.StartProject,
                    window.CreateNewProject,
                    window.ProjectName,
                    window.SelectedBucketName,
                    window.SelectedBucket,
                    window.SelectedProject));
            if (!window.ClosedByDeactivate)
            {
                ZetlForegroundService.RestoreTarget(target, Log);
                if (outcome == ZetlNoteCaptureOutcome.PasteCutBack)
                {
                    ZetlAsync.RunLogged(coordinator.PasteCutBackAsync, "paste cut back", Log);
                }
            }
        });
    }

    private void ShowCompile(
        ZetlCompileRequest request,
        object? target)
    {
        var window = new CompileWindow(
            store,
            request.Project,
            request.BucketScope)
        {
            ShowInTaskbar = false,
            DismissOnDeactivate = target is not null
        };
        ConfigureAndShowPopup(window, target, "Compile", () =>
        {
            Log(
                "Compile popup closed: "
                + $"saved={window.Saved}, "
                + $"deactivate={window.ClosedByDeactivate}.");
            var outcome = coordinator.CompleteCompile(
                request,
                new ZetlCompileResult(
                    window.Saved,
                    window.CompiledText,
                    window.SaveToBucket,
                    window.DestinationProject,
                    window.DestinationBucketName,
                    window.Flatten,
                    window.SelectedNoteTexts,
                    window.PasteNow));
            if (outcome == ZetlCompileOutcome.PasteNow)
            {
                ZetlForegroundService.RestoreTarget(target, Log);
                ZetlAsync.RunLogged(coordinator.PasteCompiledTextAsync, "paste compiled text", Log);
            }
            else if (!window.ClosedByDeactivate)
            {
                ZetlForegroundService.RestoreTarget(target, Log);
            }
        });
    }

    // Shared transient-popup wiring: register click-away dismissal, log the
    // activation lifecycle, then position and steal the foreground. The caller's
    // onClosed owns completion, foreground restore, and the close log. It runs on
    // Closing (not Closed) so the foreground restore happens while the popup
    // still owns the foreground — otherwise the window vanishes first and Windows
    // briefly flashes whatever it picks before the restore lands.
    private void ConfigureAndShowPopup(
        Window window,
        object? target,
        string logName,
        Action onClosed)
    {
        RegisterClickAwayPopup(window);
        window.Opened += (_, _) => Log($"{logName} popup opened.");
        window.Activated += (_, _) => Log($"{logName} popup activated.");
        window.Deactivated += (_, _) => Log($"{logName} popup deactivated.");
        window.Closing += (_, _) => onClosed();
        PositionAndActivate(window, target);
    }

    // Place a window near the top sixth of the working area and bring it to the
    // foreground, taking over from the captured shortcut target when present.
    private void PositionAndActivate(Window window, object? target)
    {
        ZetlWindowPlacement.FitToScreen(window);
        ZetlWindowActivation.Show(
            window,
            activationTarget: target,
            log: Log);
    }

    private async Task ShowProjectSetupAsync()
    {
        var window = new ProjectSetupWindow(store.Defaults.ProjectBuckets, templateStore.LoadAll());
        await ShowUntilClosedAsync(window);
        if (!window.Saved)
        {
            return;
        }

        if (window.SelectedTemplate is { } template)
        {
            CreateProjectFromTemplate(template, window.ProjectName);
        }
        else
        {
            store.CreateProject(
                window.ProjectName,
                window.BucketNames,
                window.ActiveBucketName);
        }

        ShowBoard(shifted: false);
    }

    // Create a project from a template through the same domain operations Kastn
    // uses — CreateProject (which applies bucket settings) plus AddSlip for any
    // consumable seeds — so both apps produce equivalent projects from one template.
    private void OnProjectServiceProjectChanged(object? sender, ZetlProjectChangedEvent change)
    {
        if (change.ChangeKind != ZetlChangeKind.Created || change.EntityKind != ZetlEntityKind.Project)
        {
            return;
        }

        var name = store.State.Projects
            .FirstOrDefault(project => project.Id == change.ProjectId)?.Name;
        if (string.IsNullOrEmpty(name)
            || string.Equals(name, ZetlStateStore.LogProjectName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dispatcher.UIThread.Post(() => notifications.Show($"Activated '{name}' in Zetl."));
    }

    private ZetlProject? CreateProjectFromTemplate(
        ZetlTemplateDocument template,
        string name,
        bool shifted = false)
    {
        // The service activates the new project in the normal lane. For the Shift
        // lane, remember the normal lane's prior active project so we can restore it
        // after moving activation to the Shift lane.
        var priorNormalActiveId = shifted ? store.GetActiveProject(shifted: false)?.Id : null;

        var response = projectService.Execute(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.CreateProject,
            template.ToCreateProjectCommand(name)));
        if (response.Status != ZetlResponseStatus.Success)
        {
            notifications.Show($"Could not create '{name}' from the {template.Name} template.");
            return null;
        }

        var snapshot = response.Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options);
        if (snapshot is null)
        {
            return null;
        }

        foreach (var bucket in template.Buckets.Where(bucket => bucket.Seeds.Count > 0))
        {
            var target = snapshot.Buckets.FirstOrDefault(
                item => string.Equals(item.Name, bucket.Name, StringComparison.Ordinal));
            if (target is null)
            {
                continue;
            }

            foreach (var text in bucket.Seeds)
            {
                projectService.Execute(ZetlCommandEnvelope.Create(
                    Guid.NewGuid().ToString("N"),
                    ZetlCommandKind.AddSlip,
                    new AddSlipCommand { BucketId = target.Id, Text = text, Source = "template" },
                    snapshot.Id));
            }
        }

        if (shifted)
        {
            // Move activation to the Shift lane and undo the normal-lane side effect.
            store.SetActiveProject(snapshot.Id, shifted: true);
            if (priorNormalActiveId is not null)
            {
                store.SetActiveProject(priorNormalActiveId, shifted: false);
            }
            else
            {
                store.ClearActiveProject(shifted: false);
            }
        }

        return store.State.Projects.FirstOrDefault(project => project.Id == snapshot.Id);
    }

    private async Task ShowSettingsAsync()
    {
        var window = new ZetlSettingsWindow(
            settingsStore.Settings,
            themeManager,
            themeStore,
            settingsStore);
        await ShowUntilClosedAsync(window);
        if (!window.Saved)
        {
            return;
        }

        var settings = settingsStore.Settings;
        settings.ToastDisplayMs = window.ToastDisplayMs;
        settings.AutoCaptureOnCopy = window.AutoCaptureOnCopy;
        settings.QuickNoteToClipboard = window.QuickNoteToClipboard;
        settings.CaptureOriginDetail = window.CaptureOriginDetail;
        settings.DefaultProjectBuckets =
            ZetlBucketDefaults.ResolveProjectBuckets(window.DefaultProjectBuckets).ToList();
        settings.DefaultCompileMode = window.DefaultCompileMode;
        settings.DefaultTsvRowLength = window.DefaultTsvRowLength;
        settingsStore.Save();
        ApplySettings();
        notifications.Show("Settings saved.");
    }

    private async void ShowFirstRunIfNeeded()
    {
        if (!settingsStore.Settings.HasSeenFirstRun)
        {
            await ShowFirstRunGuideAsync(markSeen: true);
        }
    }

    private async Task ShowFirstRunGuideAsync(bool markSeen)
    {
        var window = new FirstRunWindow();
        await ShowUntilClosedAsync(window);
        if (markSeen)
        {
            settingsStore.MarkFirstRunSeen();
        }

        if (window.OpenBoardRequested)
        {
            ShowBoard(shifted: false);
        }
    }

    private static Task ShowUntilClosedAsync(Window window)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => completion.TrySetResult();
        ZetlWindowPlacement.FitToScreen(window);
        ZetlWindowActivation.Show(window);
        return completion.Task;
    }

    private void TogglePopMode()
    {
        if (store.ActiveBucket is { } bucket
            && ZetlStateStore.IsFifoBucket(bucket))
        {
            notifications.Show("Replay buckets cannot use pop mode.");
            return;
        }

        store.ToggleActiveBucketPopMode();
        notifications.Show(store.ActiveBucket is { } active
            ? $"{active.Name} pop mode is {(active.PopMode ? "on" : "off")}."
            : "No active bucket yet.");
    }

    private void ApplySettings()
    {
        notifications.DisplayMilliseconds = settingsStore.Settings.ToastDisplayMs;
        ZetlRuntimeSettings.ApplyTo(store, settingsStore.Settings);
    }

    private void FlushLogNotes()
    {
        var batch = activityLog.Drain();
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            store.AppendLogNotes(
                batch,
                LogRetentionDays,
                LogMaxNotesPerDay);
        }
        catch (Exception ex)
        {
            Log($"Log note flush failed: {ex.Message}");
        }
    }

    private void DispatchOriginalAction(
        int virtualKey,
        bool includeShift,
        bool restoreCtrl,
        bool restoreShift)
    {
        // This runs on the keyboard hook thread, so don't await: queue the chord
        // and log the real injection result via a continuation off the hook.
        var combo = ChordlKeys.FormatComboName(virtualKey, includeShift);
        _ = keyboard.SendChord(virtualKey, includeShift, restoreCtrl, restoreShift)
            .ContinueWith(
                task => Log(task.Status == TaskStatus.RanToCompletion && task.Result
                    ? $"Sent synthetic {combo}."
                    : $"Failed to send synthetic {combo}."),
                TaskScheduler.Default);
    }

    private void WriteDiagnosticLine(string line)
    {
        try
        {
            diagnosticLines.Add(line);
        }
        catch (InvalidOperationException)
        {
            // Shutdown can complete the queue while a final callback is logging.
        }
    }

    private void ProcessDiagnosticLines()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(
                Path.GetFullPath(diagnosticLogPath))!);
            TrimDiagnosticLog();
            using var writer = new StreamWriter(
                diagnosticLogPath,
                append: true)
            {
                AutoFlush = true
            };
            foreach (var line in diagnosticLines.GetConsumingEnumerable())
            {
                writer.WriteLine(line);
            }
        }
        catch
        {
            // Diagnostics must never interfere with keyboard handling.
        }
    }

    // The log appends across every run, so cap it at startup: when it
    // outgrows 1 MB, keep only the most recent quarter so there is always
    // recent history to debug with but the file cannot grow without bound.
    private void TrimDiagnosticLog()
    {
        const long maxLogBytes = 1_000_000;
        var info = new FileInfo(diagnosticLogPath);
        if (!info.Exists || info.Length <= maxLogBytes)
        {
            return;
        }

        var bytes = File.ReadAllBytes(diagnosticLogPath);
        var keepFrom = bytes.Length - (int)(maxLogBytes / 4);
        // Start at the first whole line inside the kept window.
        while (keepFrom < bytes.Length && bytes[keepFrom - 1] != (byte)'\n')
        {
            keepFrom++;
        }

        File.WriteAllBytes(diagnosticLogPath, bytes[keepFrom..]);
    }

    private static ChordlConfiguration LoadChordlConfiguration(
        out string source)
    {
        if (ChordlConfigLoader.TryFindConfigFile(out var path))
        {
            source = $"Loaded Chordl config from {path}.";
            return ChordlConfigLoader.LoadFromFile(path);
        }

        source = "hotkeys.json not found; using embedded default Chordl config.";
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("hotkeys.json")
            ?? throw new InvalidOperationException(
                "Embedded default hotkeys.json is missing.");
        using var reader = new StreamReader(stream);
        return ChordlConfigLoader.LoadFromJson(reader.ReadToEnd());
    }

    // Builds the 16x16 tray icon: a white "Z" over a left-to-right gradient from
    // leftColor to rightColor (both 0xRRGGBB). Solid states pass the same color
    // for both ends; mode states fade green into the status hue.
    private static WindowIcon CreateTrayWindowIcon(uint leftColor, uint rightColor)
    {
        const int size = 16;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write((byte)size);
            writer.Write((byte)size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)(40 + (size * size * 4) + (size * 4)));
            writer.Write((uint)22);
            writer.Write((uint)40);
            writer.Write(size);
            writer.Write(size * 2);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)0);
            writer.Write((uint)(size * size * 4));
            writer.Write(0);
            writer.Write(0);
            writer.Write((uint)0);
            writer.Write((uint)0);

            for (var y = size - 1; y >= 0; y--)
            {
                for (var x = 0; x < size; x++)
                {
                    var zStroke = y is 3 or 12
                        ? x is >= 3 and <= 12
                        : x + y is >= 14 and <= 16 && y is > 3 and < 12;
                    var color = zStroke
                        ? 0xFFFFFFu
                        : GradientColor(leftColor, rightColor, x, size);
                    writer.Write((byte)(color & 0xFF));
                    writer.Write((byte)((color >> 8) & 0xFF));
                    writer.Write((byte)((color >> 16) & 0xFF));
                    writer.Write((byte)255);
                }
            }

            for (var index = 0; index < size * 4; index++)
            {
                writer.Write((byte)0);
            }
        }

        stream.Position = 0;
        return new WindowIcon(stream);
    }

    // Interpolates a 0xRRGGBB color across the icon width: x=0 is leftColor,
    // x=size-1 is rightColor. Equal endpoints yield a solid fill.
    private static uint GradientColor(uint left, uint right, int x, int size)
    {
        var t = size <= 1 ? 0d : x / (double)(size - 1);
        var r = Lerp((left >> 16) & 0xFF, (right >> 16) & 0xFF, t);
        var g = Lerp((left >> 8) & 0xFF, (right >> 8) & 0xFF, t);
        var b = Lerp(left & 0xFF, right & 0xFF, t);
        return (r << 16) | (g << 8) | b;
    }

    private static uint Lerp(uint a, uint b, double t)
    {
        return (uint)Math.Round(a + ((double)b - a) * t);
    }
}
