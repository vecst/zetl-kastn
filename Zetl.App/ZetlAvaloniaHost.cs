using System.Reflection;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    private const string AllowInjectedInputArgument = "--allow-injected-input-for-testing";
    private const int MaxUndoActions = 100;
    private const int LogFlushIntervalMs = 5000;
    private const int LogRetentionDays = 14;
    private const int LogMaxNotesPerDay = 2000;
    // A key event slower than this is logged on its own, not just summarized.
    private const double SlowKeyEventMs = 2;
    private static readonly TimeSpan LatencySummaryInterval = TimeSpan.FromMinutes(10);

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
    // Ends pass-through flips that have gone quiet, so the tray stops showing them.
    private readonly DispatcherTimer passThroughTimer;
    // Baseline timings for the key path and the hold path, summarized into the
    // diagnostics log (see docs/hold-routing-discussion.md, Latency).
    private readonly ZetlLatencyStats keyEventLatency = new("key event");
    private readonly ZetlLatencyStats holdLatency = new("hold to popup");
    private readonly DispatcherTimer latencySummaryTimer;
    private readonly IKeyboardBackend keyboard;
    private readonly IClipboard clipboard;
    private readonly IImageUrlResolver imageUrlResolver;
    private readonly ZetlShortcutCoordinator coordinator;
    // Between Chordl and Zetl: turns each press, tap, and hold into an action.
    private readonly ZetlGestureRouter router;
    private readonly ZetlHoldIndicator holdIndicator = new();
    private readonly HashSet<int> configuredKeyCodes = [];
    // While the hold lab is open Zetl stands down and Ctrl+C presses are timed.
    // Written on the UI thread, read on the keyboard hook thread.
    // Set while the hold lab or the tutorial is measuring the user's presses:
    // Zetl stands down and each plain Ctrl+C press is timed for it instead.
    private volatile IZetlPressMeasurer? measurer;
    private ZetlHoldLabWindow? holdLab;
    private ZetlTutorialWindow? tutorial;
    // The combo in progress, stamped on the hook thread when it starts.
    private long comboStartedAt;
    private ChordlEventContext? comboContext;
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
        ZetlTrace.EnableIfFlagged(Path.GetDirectoryName(diagnosticLogPath)!);
        diagnosticThread = new Thread(ProcessDiagnosticLines)
        {
            IsBackground = true,
            Name = "Zetl diagnostics"
        };
        diagnosticThread.Start();
        clickAwayWatcher = new ZetlClickAwayWatcher(OnClickOutsideApp);
        captureOriginProvider = new WindowsCaptureOriginProvider();

        themeManager = new ZetlThemeManager(application);
        themeManager.Apply(
            themeStore.Resolve(settingsStore.Settings.ThemeId),
            settingsStore.Settings.ThemeVariant);

        var requestedInjectedInputForTesting = Program.StartupArgs.Contains(
            AllowInjectedInputArgument,
            StringComparer.OrdinalIgnoreCase);
        var allowInjectedInputForTesting = requestedInjectedInputForTesting
            && !string.IsNullOrWhiteSpace(dataDirectory);

        notifications = new AvaloniaNotificationService(activityLog);
        keyboard = ZetlPlatformServices.CreateKeyboard(
            Log,
            allowInjectedInputForTesting);
        clipboard = ZetlPlatformServices.CreateClipboard(Log);
        imageUrlResolver = new ZetlImageUrlResolver(Log);
        var config = LoadChordlConfiguration(out var configMessage);
        configuredKeyCodes.UnionWith(config.ConfiguredKeyCodes);
        coordinator = new ZetlShortcutCoordinator(
            store,
            keyboard,
            clipboard,
            this,
            new SystemZetlDelay(),
            notifications,
            undoStack,
            () => settingsStore.Settings,
            Log,
            imageUrlResolver);
        router = new ZetlGestureRouter(
            ZetlGestureRules.Defaults,
            ZetlShellFileView.HasFocus,
            () => (ZetlOwnWindow)ZetlWindowTag.ReadForeground());
        coordinator.RegisterActions(router);

        ApplySettings();
        store.ConsolidateDefaultProject();
        store.ConsolidateDefaultProject(shifted: true);
        store.ClearActiveProject();
        store.ClearActiveProject(shifted: true);

        var repeatSuppressionDelay = TimeSpan.FromMilliseconds(settingsStore.Settings.RepeatSuppressionDelayMs);
        var holdDelay = TimeSpan.FromMilliseconds(settingsStore.Settings.HoldDelayMs);

        processor = new ChordlProcessor(
            config.Actions,
            config.ConfiguredKeyCodes,
            repeatSuppressionDelay,
            holdDelay,
            DispatchOriginalAction,
            OnPhysicalShortcutPassedThrough,
            // While the hold lab measures, taps go through untouched.
            context => measurer is null && router.OnTap(context),
            OnHoldDetected,
            Log,
            clipboard.GetChangeToken,
            OnComboStarted,
            OnComboEnded);

        trayIcon = CreateTrayIcon();
        // Reflect Zetl's state in the tray icon. store.Changed covers active
        // project and bucket kind, PassThroughChanged a held Ctrl+P; UpdateTrayIcon
        // no-ops when the effective state is unchanged, so this stays cheap
        // despite firing often.
        store.Changed += (_, _) => Dispatcher.UIThread.Post(UpdateTrayIcon);
        coordinator.PassThroughChanged += () => Dispatcher.UIThread.Post(UpdateTrayIcon);
        passThroughTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        passThroughTimer.Tick += (_, _) => coordinator.ExpirePassThroughFlips();
        passThroughTimer.Start();
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
        latencySummaryTimer = new DispatcherTimer { Interval = LatencySummaryInterval };
        latencySummaryTimer.Tick += (_, _) => LogLatencySummaries();
        latencySummaryTimer.Start();
        ipcServer.Start();

        // The log's per-line stamps carry no date, so mark each start with one.
        // The build's version, with its commit, so a log or crash report names
        // exactly which build it came from.
        var version = typeof(ZetlAvaloniaHost).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "unknown";
        Log($"Zetl {version} started {DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
        Log(configMessage);
        if (allowInjectedInputForTesting)
        {
            Log(
                "Injected keyboard events are enabled for testing; "
                + "Zetl replay events remain ignored.");
        }
        else if (requestedInjectedInputForTesting)
        {
            Log(
                "Ignoring --allow-injected-input-for-testing because it requires "
                + "--data-dir.");
        }

        if (!keyboard.Start(TimedHandleKeyEvent))
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
        passThroughTimer.Stop();
        latencySummaryTimer.Stop();
        LogLatencySummaries();
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
        menu.Items.Add(Item("Take the Tour", () => _ = ShowTutorialAsync()));
        menu.Items.Add(Item("Measure My Taps and Holds", () => _ = ShowHoldLabAsync()));
        menu.Items.Add(Item("Notification History", notifications.ShowHistory));
        menu.Items.Add(Item("Clear Notification History", notifications.ClearHistory));
        menu.Items.Add(Item("Pass-through On/Off for Now", () => coordinator.TogglePassThrough(shifted: false)));
        menu.Items.Add(Item("Settings", () => _ = ShowSettingsAsync()));
        menu.Items.Add(new NativeMenuItemSeparator());
        // Unified tray: launch Kastn when it is not running, or focus/restore it
        // when it is hidden after window close.
        menu.Items.Add(Item("Open Kastn", OpenKastn));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Quit", RequestQuit));

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
    // two lanes differ (Replay > PassThrough > Active > Idle).
    private enum TrayIconState
    {
        Idle = 0,
        Active = 1,
        PassThrough = 2,
        Replay = 3
    }

    // White Z over a left-to-right gradient. Idle is a muted grey-periwinkle
    // (solid). Active is solid green. Replay and a held Ctrl+P's pass-through
    // fade green into the status hue (purple for replay, orange for
    // pass-through), so "active" reads on the left while the mode shows on the
    // right.
    private const uint TrayIdleColor = 0x7B779C;
    private const uint TrayActiveColor = 0x2FA565;
    private const uint TrayReplayColor = 0x9B4FD6;
    private const uint TrayPassThroughColor = 0xEE8A2D;

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

        if (ZetlStateStore.IsReplayBucket(bucket))
        {
            return TrayIconState.Replay;
        }

        // Orange only for pass-through switched on for now; the setting itself
        // is the normal state and doesn't tint the icon.
        return coordinator.IsPassThroughFlipped(shifted) && coordinator.IsPassThroughOn(shifted)
            ? TrayIconState.PassThrough
            : TrayIconState.Active;
    }

    private void UpdateTrayIcon()
    {
        if (disposed)
        {
            return;
        }

        var state = ComputeTrayState();
        var tooltip = TrayTooltip(state);
        if (state == currentTrayState && tooltip == trayIcon.ToolTipText)
        {
            return;
        }

        currentTrayState = state;
        trayIcon.Icon = TrayIconForState(state);
        trayIcon.ToolTipText = tooltip;
    }

    private WindowIcon TrayIconForState(TrayIconState state)
    {
        if (!trayIcons.TryGetValue(state, out var icon))
        {
            icon = state switch
            {
                TrayIconState.Active => CreateTrayWindowIcon(TrayActiveColor, TrayActiveColor),
                TrayIconState.Replay => CreateTrayWindowIcon(TrayActiveColor, TrayReplayColor),
                TrayIconState.PassThrough => CreateTrayWindowIcon(TrayActiveColor, TrayPassThroughColor),
                _ => CreateTrayWindowIcon(TrayIdleColor, TrayIdleColor)
            };
            trayIcons[state] = icon;
        }

        return icon;
    }

    // Grey means no project is armed; the idle-copy setting says whether tapped
    // copies are still being captured (to the Journal) or left alone.
    private string TrayTooltip(TrayIconState state)
    {
        return state switch
        {
            TrayIconState.Active => "Zetl — project active",
            TrayIconState.Replay => "Zetl — replay mode",
            TrayIconState.PassThrough => "Zetl — pass-through on for now",
            _ => ZetlIdleCopyCapture.CapturesToJournal(store.Defaults.IdleCopyCapture)
                ? "Zetl — capturing copies to the Journal"
                : "Zetl — not capturing copies"
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

    // Open Kastn from the tray: focus/restore a connected Kastn, including one
    // hidden after window close, over the control pipe, or launch it when none is
    // running.
    private async void OpenKastn()
    {
        if (ipcServer.HasClient("Kastn"))
        {
            try
            {
                await KastnControlChannel.ActivateAsync(null);
                return;
            }
            catch (Exception ex)
            {
                // Kastn may have vanished between the check and the signal; fall
                // through to launching a fresh instance.
                Log($"Show Kastn failed ({ex.GetType().Name}): {ex.Message}");
            }
        }

        try
        {
            ZetlKastnLauncher.Launch(explicitPath: kastnPath, pipeName: ipcPipeName);
            Log("Launched Kastn from the tray.");
        }
        catch (Exception ex)
        {
            Log($"Open Kastn failed ({ex.GetType().Name}): {ex.Message}");
            notifications.Show($"Could not open Kastn: {ex.Message}");
        }
    }

    // Coordinated quit. With no Kastn connected, Zetl just shuts down. With Kastn
    // connected, Zetl asks it to close too. Kastn raises itself for confirmation,
    // and a cancel there aborts Zetl's quit, so closing Zetl no longer silently
    // relaunches because Kastn was still open.
    private async void RequestQuit()
    {
        if (disposed)
        {
            return;
        }

        if (!ipcServer.HasClient("Kastn"))
        {
            desktop.Shutdown();
            return;
        }

        KastnShutdownDecision decision;
        try
        {
            decision = await KastnControlChannel.RequestShutdownAsync();
        }
        catch (Exception ex)
        {
            // Don't trap the user in an unquittable Zetl if the signal fails.
            Log($"Kastn shutdown request failed ({ex.GetType().Name}): {ex.Message}");
            decision = KastnShutdownDecision.NoKastn;
        }

        if (decision == KastnShutdownDecision.Cancel)
        {
            Log("Quit cancelled at Kastn's confirmation.");
            return;
        }

        desktop.Shutdown();
    }

    // The keyboard hook's call into Chordl, timed. This is the path that must
    // stay instant, so recording is a few interlocked operations and only an
    // unusually slow event is logged individually.
    private bool TimedHandleKeyEvent(int virtualKey, bool keyDown, bool keyUp, bool isRepeat)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            // Trace only the keys Chordl handles plus the modifiers, so ordinary
            // typing is neither slowed nor written down.
            if (ZetlTrace.Enabled
                && (configuredKeyCodes.Contains(virtualKey)
                    || ChordlKeys.IsControlKey(virtualKey)
                    || ChordlKeys.IsShiftKey(virtualKey)))
            {
                ZetlTrace.Write($"key vk=0x{virtualKey:X2} {(keyDown ? "down" : "up")}{(isRepeat ? " repeat" : "")} begin");
                var handled = processor!.HandleKeyEvent(virtualKey, keyDown, keyUp, isRepeat);
                ZetlTrace.Write($"key vk=0x{virtualKey:X2} end handled={handled}");
                return handled;
            }

            return processor!.HandleKeyEvent(virtualKey, keyDown, keyUp, isRepeat);
        }
        finally
        {
            var elapsed = Stopwatch.GetTimestamp() - started;
            keyEventLatency.Record(elapsed);
            var milliseconds = ZetlLatencyStats.ToMilliseconds(elapsed);
            if (milliseconds >= SlowKeyEventMs)
            {
                Log($"Slow key event: {milliseconds:0.00} ms (vk=0x{virtualKey:X2}, {(keyDown ? "down" : "up")}).");
            }
        }
    }

    private void LogLatencySummaries()
    {
        foreach (var stats in new[] { keyEventLatency, holdLatency })
        {
            if (stats.TakeSummary() is { } summary)
            {
                Log($"Latency {DateTime.Now:yyyy-MM-dd} {summary}.");
            }
        }
    }

    // Chordl's combo lifetime, for the hold indicator. These run on the keyboard
    // hook thread under Chordl's lock, so they only stamp the time and post.
    private void OnComboStarted(ChordlEventContext context)
    {
        var started = Stopwatch.GetTimestamp();
        comboStartedAt = started;
        comboContext = context;
        if (measurer is not null)
        {
            // The indicator would nudge measured holds toward the threshold.
            return;
        }

        Dispatcher.UIThread.Post(() => holdIndicator.Start(
            context.Name,
            started,
            TimeSpan.FromMilliseconds(settingsStore.Settings.HoldDelayMs),
            HoldActionLabel(context)));
    }

    private void OnComboEnded(bool held)
    {
        if (measurer is { } timing)
        {
            // Time plain Ctrl+C presses, key down to release, for the measurer.
            if (comboContext is { KeyCode: ChordlKeys.VK_C, ShiftLane: false })
            {
                var pressed = Stopwatch.GetElapsedTime(comboStartedAt).TotalMilliseconds;
                Dispatcher.UIThread.Post(() => timing.RecordPress(pressed));
            }

            return;
        }

        Dispatcher.UIThread.Post(() => holdIndicator.End(held));
    }

    // The hold lab: Zetl's actions pause while it is open so the user's own
    // taps and holds can be measured, then it can apply a new hold delay.
    private async Task ShowHoldLabAsync()
    {
        if (holdLab is { } open)
        {
            ZetlWindowActivation.Show(open);
            return;
        }

        var lab = new ZetlHoldLabWindow(settingsStore.Settings.HoldDelayMs, ApplyHoldDelay, Log);
        holdLab = lab;
        measurer = lab.Panel;
        try
        {
            await ShowUntilClosedAsync(lab);
        }
        finally
        {
            holdLab = null;
            if (measurer == lab.Panel)
            {
                measurer = null;
            }
        }
    }

    private void ApplyHoldDelay(int milliseconds)
    {
        settingsStore.Settings.HoldDelayMs = milliseconds;
        settingsStore.Save();
        ApplySettings();
        notifications.Show($"Hold threshold set to {milliseconds} ms.");
    }

    // The guided tour. It measures through the same stand-down as the lab and
    // hears about saved notes, pasted-back cuts, and the like from TellTutorial.
    private async Task ShowTutorialAsync()
    {
        if (tutorial is { } open)
        {
            ZetlWindowActivation.Show(open);
            return;
        }

        var window = new ZetlTutorialWindow(new ZetlTutorialHost(
            settingsStore.Settings.HoldDelayMs,
            settingsStore.Settings.HoldIndicatorStyle,
            ApplyHoldDelay,
            style =>
            {
                settingsStore.Settings.HoldIndicatorStyle = style;
                settingsStore.Save();
                ApplySettings();
            },
            panel => measurer = panel,
            () =>
            {
                settingsStore.Settings.TutorialState = ZetlTutorialState.Completed;
                settingsStore.Save();
            },
            Log));
        tutorial = window;
        try
        {
            await ShowUntilClosedAsync(window);
        }
        finally
        {
            tutorial = null;
            measurer = null;
            // Closing before the last hold counts as skipping, unless an
            // earlier run already completed it. Either way first run is over.
            if (settingsStore.Settings.TutorialState != ZetlTutorialState.Completed)
            {
                settingsStore.Settings.TutorialState = ZetlTutorialState.Skipped;
            }

            Log($"Tutorial closed: {settingsStore.Settings.TutorialState}.");
            settingsStore.MarkFirstRunSeen();
        }
    }

    private void TellTutorial(ZetlTutorialSignal signal) =>
        Dispatcher.UIThread.Post(() => tutorial?.OnSignal(signal));

    // What holding this chord would do, or null when it would do nothing.
    private string? HoldActionLabel(ChordlEventContext context)
    {
        var actionId = router.Resolve(ZetlGestureKind.Hold, context.KeyCode);
        return actionId == ZetlGestureActions.Native
            ? null
            : ZetlGestureActions.ChoicesFor(ZetlGestureKind.Hold)
                .FirstOrDefault(choice => choice.Id == actionId)?.Label;
    }

    private void OnHoldDetected(ChordlEventContext context)
    {
        if (measurer is not null)
        {
            // Measuring: cancel the copy's pending auto-capture, open nothing.
            coordinator.ClaimPendingForHold(context);
            return;
        }

        var holdStarted = Stopwatch.GetTimestamp();
        Dispatcher.UIThread.Post(holdIndicator.Complete);
        var pending = coordinator.ClaimPendingForHold(context);
        var target = TakeShortcutTarget(context.KeyCode)
            ?? ZetlForegroundService.CaptureTarget();
        Log($"{context.Name} hold target: {ZetlForegroundService.DescribeTarget(target)}.");
        Dispatcher.UIThread.Post(() => ZetlAsync.RunLogged(
            () => HandleHoldAsync(context, target, pending, holdStarted),
            "hold action",
            Log));
    }

    private void OnPhysicalShortcutPassedThrough(ChordlEventContext context)
    {
        if (measurer is not null)
        {
            // Measuring: the copy goes through natively and is never captured.
            return;
        }

        if (context.KeyCode is ChordlKeys.VK_C or ChordlKeys.VK_X)
        {
            // Only cheap facts are read here on the hook thread; the origin's
            // application name resolves later, when a capture first needs it.
            var detail = settingsStore.Settings.CaptureOriginDetail;
            ZetlTrace.Write("press: capture target begin");
            var originTarget = captureOriginProvider.CaptureTarget(detail);
            ZetlTrace.Write("press: capture target end");
            var captureOrigin = new Lazy<ZetlCaptureOrigin?>(() => originTarget is null
                ? null
                : captureOriginProvider.Describe(originTarget, detail));
            var target = ZetlForegroundService.CaptureTarget();
            lock (shortcutTargetsGate)
            {
                shortcutTargets[context.KeyCode] = target;
            }

            Log($"{context.Name} keydown target: {ZetlForegroundService.DescribeTarget(target)}.");
            ZetlTrace.Write("press: router begin");
            ZetlAsync.RunLogged(
                () => router.OnPressAsync(context, captureOrigin),
                "physical shortcut pass-through",
                Log);
            return;
        }

        ZetlAsync.RunLogged(
            () => router.OnPressAsync(context),
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
        ZetlPendingShortcut? pending,
        long holdStarted)
    {
        var request = await router.OnHoldAsync(context, pending);
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

        holdLatency.Record(Stopwatch.GetTimestamp() - holdStarted);
    }

    // Zetl's quick template access: a held Ctrl+T lets the user start a fresh
    // project from a template, opening on capture templates with a switcher for
    // consumable ones.
    private void ShowTemplatePicker(ZetlTemplatePickerRequest request, object? target)
    {
        var templates = templateStore.LoadAll();
        if (templates.Count == 0)
        {
            // Built-ins always ship some, so this is rare.
            notifications.Show("No templates yet. Create one in Kastn.");
            return;
        }

        var window = new TemplatePickerWindow(templates, settingsStore.Settings.LastTemplateId)
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
        TellTutorial(ZetlTutorialSignal.BoardOpened);
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
            projectWasActive: request.ProjectWasActive,
            activateByDefault: request.StartProjectDefault,
            image: request.Image)
        {
            ShowInTaskbar = false,
            DismissOnDeactivate = target is not null
        };
        ConfigureAndShowPopup(window, target, "Capture", () =>
        {
            Log(
                "Capture popup closed: "
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
            if (window.Saved)
            {
                TellTutorial(string.Equals(request.Source, "cut", StringComparison.OrdinalIgnoreCase)
                    ? ZetlTutorialSignal.QuickNoteSaved
                    : ZetlTutorialSignal.CaptureSaved);
            }

            if (!window.ClosedByDeactivate)
            {
                ZetlForegroundService.RestoreTarget(target, Log);
                if (outcome == ZetlNoteCaptureOutcome.PasteCutBack)
                {
                    ZetlAsync.RunLogged(
                        async () =>
                        {
                            await coordinator.PasteCutBackAsync();
                            TellTutorial(ZetlTutorialSignal.CutPastedBack);
                        },
                        "paste cut back",
                        Log);
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
            request.BucketScope,
            settingsStore.Settings.ComposeCtrlEnterPastes,
            settingsStore.Settings.ComposeHeadings)
        {
            ShowInTaskbar = false,
            DismissOnDeactivate = target is not null
        };
        ConfigureAndShowPopup(window, target, "Compose", () =>
        {
            if (window.Headings != settingsStore.Settings.ComposeHeadings)
            {
                settingsStore.Settings.ComposeHeadings = window.Headings;
                settingsStore.Save();
            }

            Log(
                "Compose popup closed: "
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
                    window.PasteNow,
                    window.CompiledHtml));
            if (outcome == ZetlCompileOutcome.PasteNow)
            {
                ZetlForegroundService.RestoreTarget(target, Log);
                ZetlAsync.RunLogged(
                    async () =>
                    {
                        await coordinator.PasteCompiledTextAsync();
                        TellTutorial(ZetlTutorialSignal.CompilePasted);
                    },
                    "paste compiled text",
                    Log);
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
        ZetlWindowPlacement.CaptureContext(window, ZetlForegroundService.GetWindowsHandle(target));
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
        var lane = shifted ? ZetlStateStore.ShiftLane : ZetlStateStore.NormalLane;
        var response = projectService.Execute(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.CreateProject,
            template.ToCreateProjectCommand(name, lane) with { ActivateShifted = shifted }));
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

        foreach (var bucket in template.Buckets.Where(
            bucket => bucket.Seeds.Count > 0 || bucket.Cards.Count > 0))
        {
            var target = snapshot.Buckets.FirstOrDefault(
                item => string.Equals(item.Name, bucket.Name, StringComparison.Ordinal));
            if (target is null)
            {
                continue;
            }

            var cards = bucket.Seeds
                .Select(text => new ZetlTemplateSlipDocument { Text = text })
                .Concat(bucket.Cards);
            foreach (var card in cards)
            {
                projectService.Execute(ZetlCommandEnvelope.Create(
                    Guid.NewGuid().ToString("N"),
                    ZetlCommandKind.AddSlip,
                    new AddSlipCommand
                    {
                        BucketId = target.Id,
                        Title = card.Title,
                        Text = card.Text,
                        Source = "template"
                    },
                    snapshot.Id));
            }
        }

        settingsStore.Settings.LastTemplateId = template.Id;
        settingsStore.Save();
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
        settings.PassThrough = window.PassThrough;
        settings.QuickNoteToClipboard = window.QuickNoteToClipboard;
        settings.ReplayResumeClipboard = window.ReplayResumeClipboard;
        settings.CaptureOriginDetail = window.CaptureOriginDetail;
        settings.DefaultProjectBuckets =
            ZetlBucketDefaults.ResolveProjectBuckets(window.DefaultProjectBuckets).ToList();
        settings.DefaultCompileMode = window.DefaultCompileMode;
        settings.ComposeCtrlEnterPastes = window.ComposeCtrlEnterPastes;
        settings.DefaultTsvRowLength = window.DefaultTsvRowLength;
        settings.DayStartHour = window.DayStartHour;
        settings.JournalAutoReturnHours = window.JournalAutoReturnHours;
        settings.IdleCopyCapture = window.IdleCopyCapture;
        settings.HoldActionRules = window.HoldActionRules;
        settings.PopupPosition = window.PopupPosition;
        settings.PopupOpacityPercent = window.PopupOpacityPercent;
        settings.HoldIndicatorStyle = window.HoldIndicatorStyle;
        settings.HoldIndicatorPosition = window.HoldIndicatorPosition;
        settings.JournalInterval = window.JournalInterval;
        settings.KastnAutosave = window.KastnAutosave;
        settings.KastnStartup = window.KastnStartup;
        settings.KastnDefaultViewId = window.KastnDefaultViewId;
        settings.KastnMainLaneLabel = window.KastnMainLaneLabel;
        settings.KastnAlternateLaneLabel = window.KastnAlternateLaneLabel;
        settings.KastnMinimizeAfterTemplate = window.KastnMinimizeAfterTemplate;
        settings.KastnTemporaryTemplateLaneDefault = window.KastnTemporaryTemplateLaneDefault;
        settings.KastnCloseToTray = window.KastnCloseToTray;
        settings.KastnPreferSlipKindOverBucketKind = window.KastnPreferSlipKindOverBucketKind;

        // Map new advanced settings
        settings.LogRetentionDays = window.LogRetentionDays;
        settings.LogMaxNotesPerDay = window.LogMaxNotesPerDay;
        settings.LogFlushIntervalMs = window.LogFlushIntervalMs;
        settings.MaxUndoActions = window.MaxUndoActions;
        settings.UntitledSlipTitle = window.UntitledSlipTitle;
        settings.MaxSlipLabelLength = window.MaxSlipLabelLength;
        settings.PdfPageFormat = window.PdfPageFormat;
        settings.PdfFontSize = window.PdfFontSize;
        settings.HoldDelayMs = window.HoldDelayMs;
        settings.RepeatSuppressionDelayMs = window.RepeatSuppressionDelayMs;
        settings.ClipboardPollIntervalMs = window.ClipboardPollIntervalMs;
        settings.ClipboardObservationTimeoutMs = window.ClipboardObservationTimeoutMs;
        settings.AutoCaptureClipboardTimeoutMs = window.AutoCaptureClipboardTimeoutMs;
        settings.PopClipboardDelayMs = window.PopClipboardDelayMs;
        settings.ReplayClipboardRestoreDelayMs = window.ReplayClipboardRestoreDelayMs;
        settings.DownloadTimeoutSeconds = window.DownloadTimeoutSeconds;

        settingsStore.Save();
        ApplySettings();
        // The idle tooltip names the idle-copy setting, which may have just changed.
        UpdateTrayIcon();
        notifications.Show("Settings saved.");
    }

    // A new install opens the tour; it stays in the tray menu afterwards.
    private void ShowFirstRunIfNeeded()
    {
        if (!settingsStore.Settings.HasSeenFirstRun)
        {
            ZetlAsync.RunLogged(ShowTutorialAsync, "first-run tour", Log);
        }
    }

    private Task ShowUntilClosedAsync(Window window)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => completion.TrySetResult();
        ZetlWindowReveal.WhenReady(
            window,
            elapsed => Log($"{window.Title} ready in {elapsed.TotalMilliseconds:0} ms."));
        ZetlWindowPlacement.FitToScreen(window);
        ZetlWindowActivation.Show(window);
        return completion.Task;
    }

    private void ApplySettings()
    {
        notifications.DisplayMilliseconds = settingsStore.Settings.ToastDisplayMs;
        ZetlRuntimeSettings.ApplyTo(store, settingsStore.Settings);
        router.SetRules(ZetlGestureRules.Apply(settingsStore.Settings.HoldActionRules));
        ZetlWindowPlacement.PopupPosition = ZetlScreenAnchor.Normalize(settingsStore.Settings.PopupPosition);
        ZetlWindowPlacement.PopupOpacityPercent = ZetlPopupOpacity.Clamp(settingsStore.Settings.PopupOpacityPercent);
        var indicatorStyle = ZetlHoldIndicatorStyle.Normalize(settingsStore.Settings.HoldIndicatorStyle);
        holdIndicator.Enabled = indicatorStyle != ZetlHoldIndicatorStyle.Off;
        holdIndicator.Detailed = indicatorStyle == ZetlHoldIndicatorStyle.Detailed;
        holdIndicator.Anchor = ZetlHoldIndicatorPosition.Resolve(
            settingsStore.Settings.HoldIndicatorPosition,
            settingsStore.Settings.PopupPosition);
        if (logFlushTimer is not null)
        {
            logFlushTimer.Interval = TimeSpan.FromMilliseconds(settingsStore.Settings.LogFlushIntervalMs);
        }
        if (undoStack is not null)
        {
            undoStack.Capacity = settingsStore.Settings.MaxUndoActions;
            undoStack.Truncate();
        }
        if (processor is not null)
        {
            processor.RepeatSuppressionDelay = TimeSpan.FromMilliseconds(settingsStore.Settings.RepeatSuppressionDelayMs);
            processor.HoldDelay = TimeSpan.FromMilliseconds(settingsStore.Settings.HoldDelayMs);
        }
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
            store.AppendLogSlips(
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
    private static WindowIcon CreateTrayWindowIcon(uint leftColor, uint rightColor) =>
        ZetlGlyphIcon.Create(size: 16, leftColor, rightColor, IsTrayGlyph);

    // A 16px "Z": two horizontal bars joined by a diagonal.
    private static bool IsTrayGlyph(int x, int y) =>
        y is 3 or 12
            ? x is >= 3 and <= 12
            : x + y is >= 14 and <= 16 && y is > 3 and < 12;
}
