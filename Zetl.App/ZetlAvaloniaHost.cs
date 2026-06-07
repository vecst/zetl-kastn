using System.Reflection;
using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Chordl;

namespace ZETL;

internal sealed class ZetlAvaloniaHost : IZetlDispatcher, IDisposable
{
    private const int MaxUndoActions = 100;
    private const int LogFlushIntervalMs = 5000;
    private const int LogRetentionDays = 14;
    private const int LogMaxNotesPerDay = 2000;

    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly ZetlStateStore store;
    private readonly ZetlAppSettingsStore settingsStore;
    private readonly ZetlThemeStore themeStore;
    private readonly ZetlActivityLogBuffer activityLog = new();
    private readonly AvaloniaNotificationService notifications;
    private readonly ZetlUndoStack undoStack = new(MaxUndoActions);
    private readonly List<string> logLines = [];
    private readonly DispatcherTimer logFlushTimer;
    private readonly IKeyboardBackend keyboard;
    private readonly IClipboard clipboard;
    private readonly ZetlShortcutCoordinator coordinator;
    private readonly ChordlProcessor? processor;
    private readonly TrayIcon trayIcon;
    private readonly ZetlThemeManager themeManager;
    private readonly Dictionary<bool, BoardWindow> boards = [];
    private readonly object shortcutTargetsGate = new();
    private readonly Dictionary<int, object?> shortcutTargets = [];
    private readonly string diagnosticLogPath;
    private readonly BlockingCollection<string> diagnosticLines = [];
    private readonly Thread diagnosticThread;
    private bool disposed;

    public ZetlAvaloniaHost(
        Application application,
        IClassicDesktopStyleApplicationLifetime desktop,
        string? dataDirectory = null)
    {
        this.desktop = desktop;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        store = dataDirectory is null
            ? new ZetlStateStore()
            : new ZetlStateStore(Path.Combine(dataDirectory, "state.json"));
        settingsStore = new ZetlAppSettingsStore(dataDirectory is null
            ? null
            : Path.Combine(dataDirectory, "settings.json"));
        themeStore = new ZetlThemeStore(dataDirectory is null
            ? null
            : Path.Combine(dataDirectory, "themes"));
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

        themeManager = new ZetlThemeManager(application, settingsStore);
        themeManager.Apply(
            themeStore.Resolve(settingsStore.Settings.ThemeId),
            settingsStore.Settings.ThemeVariant,
            persist: false);

        notifications = new AvaloniaNotificationService(activityLog);
        keyboard = ZetlPlatformServices.CreateKeyboard(Log);
        clipboard = ZetlPlatformServices.CreateClipboard(Log);

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
            config.HoldDelay);

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
        logFlushTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(LogFlushIntervalMs)
        };
        logFlushTimer.Tick += (_, _) => FlushLogNotes();
        logFlushTimer.Start();

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
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        logLines.Add(line);
        if (logLines.Count > 200)
        {
            logLines.RemoveAt(0);
        }

        WriteDiagnosticLine(line);
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
        diagnosticLines.CompleteAdding();
        diagnosticThread.Join(TimeSpan.FromSeconds(1));
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

        var icon = new TrayIcon
        {
            Icon = CreateTrayWindowIcon(),
            ToolTipText = "Zetl",
            Menu = menu,
            IsVisible = true
        };
        icon.Clicked += (_, _) => ShowBoard(shifted: false);
        return icon;
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
        Dispatcher.UIThread.Post(async () =>
            await HandleHoldAsync(context, target, pending));
    }

    private void OnPhysicalShortcutPassedThrough(ChordlEventContext context)
    {
        if (context.KeyCode is ChordlKeys.VK_C or ChordlKeys.VK_X)
        {
            var target = ZetlForegroundService.CaptureTarget();
            lock (shortcutTargetsGate)
            {
                shortcutTargets[context.KeyCode] = target;
            }

            Log($"{context.Name} keydown target: {ZetlForegroundService.DescribeTarget(target)}.");
        }

        _ = coordinator.OnPhysicalShortcutPassedThroughAsync(context);
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
        }
    }

    private void ShowBoard(bool shifted, object? target = null)
    {
        if (!boards.TryGetValue(shifted, out var board))
        {
            board = new BoardWindow(store, shifted)
            {
                ShowInTaskbar = false
            };
            var lane = shifted;
            board.Closed += (_, _) => boards.Remove(lane);
            boards[lane] = board;
        }

        board.AutoHideOnDeactivate = target is not null;
        board.ShowActiveProject();
        PositionNearTopSixth(board);
        ZetlWindowActivation.Show(
            board,
            activationTarget: target,
            log: Log);
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
            request.ShowStartProjectToggle,
            request.StartProjectDefault,
            request.ScratchOnlyUntilProjectStarted,
            request.CreateNewProjectToggle,
            request.ProjectToggleText,
            request.ProjectNameDefault)
        {
            ShowInTaskbar = false,
            CommitOnDeactivate = target is not null
        };
        window.Opened += (_, _) => Log("Note popup opened.");
        window.Activated += (_, _) => Log("Note popup activated.");
        window.Deactivated += (_, _) => Log("Note popup deactivated.");
        window.Closed += (_, _) =>
        {
            Log(
                "Note popup closed: "
                + $"saved={window.Saved}, "
                + $"deactivate={window.ClosedByDeactivate}.");
            coordinator.CompleteNoteCapture(
                request,
                new ZetlNoteCaptureResult(
                    window.Saved,
                    window.NoteText,
                    window.StartProject,
                    window.CreateNewProject,
                    window.ProjectName,
                    window.SelectedBucketName,
                    window.SelectedBucket));
            if (!window.ClosedByDeactivate)
            {
                ZetlForegroundService.RestoreTarget(target);
            }
        };
        PositionNearTopSixth(window);
        ZetlWindowActivation.Show(
            window,
            activationTarget: target,
            log: Log);
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
            CloseOnDeactivate = target is not null
        };
        window.Opened += (_, _) => Log("Compile popup opened.");
        window.Activated += (_, _) => Log("Compile popup activated.");
        window.Deactivated += (_, _) => Log("Compile popup deactivated.");
        window.Closed += (_, _) =>
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
                ZetlForegroundService.RestoreTarget(target);
                _ = coordinator.PasteCompiledTextAsync();
            }
            else if (!window.ClosedByDeactivate)
            {
                ZetlForegroundService.RestoreTarget(target);
            }
        };
        PositionNearTopSixth(window);
        ZetlWindowActivation.Show(
            window,
            activationTarget: target,
            log: Log);
    }

    private async Task ShowProjectSetupAsync()
    {
        var window = new ProjectSetupWindow(store.Defaults.ProjectBuckets);
        await ShowUntilClosedAsync(window);
        if (!window.Saved)
        {
            return;
        }

        store.CreateProject(
            window.ProjectName,
            window.BucketNames,
            window.ActiveBucketName);
        ShowBoard(shifted: false);
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
        settings.DefaultProjectBuckets = window.DefaultProjectBuckets.Count > 0
            ? window.DefaultProjectBuckets
            : ["Inbox", "Scratch"];
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
        PositionNearTopSixth(window);
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
        var sent = keyboard.SendChord(
            virtualKey,
            includeShift,
            restoreCtrl,
            restoreShift);
        Log(sent
            ? $"Sent synthetic {ChordlKeys.FormatComboName(virtualKey, includeShift)}."
            : $"Failed to send synthetic {ChordlKeys.FormatComboName(virtualKey, includeShift)}.");
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

    private static void PositionNearTopSixth(Window window)
    {
        var workingArea = window.Screens.Primary?.WorkingArea;
        if (workingArea is not { } area)
        {
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(
            area.X + Math.Max(0, (area.Width - (int)window.Width) / 2),
            area.Y + Math.Max(24, area.Height / 6));
    }

    private static WindowIcon CreateTrayWindowIcon()
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
                    writer.Write((byte)(zStroke ? 255 : 214));
                    writer.Write((byte)(zStroke ? 255 : 92));
                    writer.Write((byte)(zStroke ? 255 : 110));
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
}
