using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;

namespace ZETL;

public partial class App : Application
{
    private ZetlAvaloniaHost? host;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var themeArgument = Program.StartupArgs
            .FirstOrDefault(arg => arg.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
            ?["--theme=".Length..]
            .ToLowerInvariant();
        var themeIdArgument = Program.StartupArgs
            .FirstOrDefault(arg => arg.StartsWith(
                "--theme-id=",
                StringComparison.OrdinalIgnoreCase))
            ?["--theme-id=".Length..];

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var preview = Program.StartupArgs
                .FirstOrDefault(arg => arg.StartsWith("--preview=", StringComparison.OrdinalIgnoreCase))
                ?["--preview=".Length..]
                .ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(preview))
            {
                var dataDirectory = Program.StartupArgs
                    .FirstOrDefault(arg => arg.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
                    ?["--data-dir=".Length..];
                host = new ZetlAvaloniaHost(this, desktop, dataDirectory);
                desktop.Exit += (_, _) =>
                {
                    host?.Dispose();
                    host = null;
                };
                if (Program.StartupArgs.Contains(
                    "--open-board",
                    StringComparer.OrdinalIgnoreCase))
                {
                    Dispatcher.UIThread.Post(() => host?.OpenBoard());
                }
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // DEV HARNESS: preview windows against throwaway data.
            // Each instance gets its own state directory so concurrent
            // previews never contend on the same files.
            // --popup-position= and --popup-opacity= preview the popup
            // appearance settings without touching a real profile.
            if (PreviewArgument("--popup-position=") is { } previewPosition)
            {
                ZetlWindowPlacement.PopupPosition = ZetlScreenAnchor.Normalize(previewPosition);
            }

            if (int.TryParse(PreviewArgument("--popup-opacity="), out var previewOpacity))
            {
                ZetlWindowPlacement.PopupOpacityPercent = ZetlPopupOpacity.Clamp(previewOpacity);
            }

            var stateDir = Path.Combine(
                Path.GetTempPath(),
                "ZetlAvaloniaPreview",
                $"instance-{Environment.ProcessId}");
            desktop.Exit += (_, _) =>
            {
                try
                {
                    Directory.Delete(stateDir, recursive: true);
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException)
                {
                    // Leftover throwaway data in the temp directory is fine.
                }
            };
            var settingsStore = new ZetlAppSettingsStore(
                Path.Combine(stateDir, "settings.json"));
            var themeStore = new ZetlThemeStore(
                Path.Combine(stateDir, "themes"));
            var templateStore = CreatePreviewTemplateStore(stateDir);
            var themeManager = new ZetlThemeManager(this);
            themeManager.Apply(
                themeStore.Resolve(themeIdArgument ?? settingsStore.Settings.ThemeId),
                themeArgument is "light" or "dark"
                    ? themeArgument
                    : settingsStore.Settings.ThemeVariant);
            var store = new ZetlStateStore(Path.Combine(stateDir, "state.json"));
            ZetlProject? CreatePreviewProject(
                ZetlTemplateDocument template,
                string name,
                bool shifted) => store.CreateProject(
                    name,
                    template.Buckets.Select(item => item.Name),
                    template.Buckets[0].Name,
                    shifted);
            var project = store.GetActiveProject()
                ?? store.CreateProject("Preview", new[] { "Inbox", "Ideas", "Scratch" }, "Inbox");
            var bucket = store.GetActiveBucket()
                ?? project.Buckets.First();
            bucket.Settings.DefaultKind = "Replay";
            bucket.Settings.DefaultCompileMode = "TSV";
            bucket.Settings.DefaultStartingText = "Name\nEmail\nStatus";
            bucket.Settings.DefaultTsvRowLength = 3;
            var notifications = new[]
            {
                new ZetlNotificationEntry(DateTime.Now.AddMinutes(-8), "Captured to Inbox in Preview."),
                new ZetlNotificationEntry(DateTime.Now.AddMinutes(-3), "Queue replay is on."),
                new ZetlNotificationEntry(DateTime.Now, "Compiled to Scratch in Preview.")
            };
            if (bucket.Slips.Count == 0)
            {
                store.AddSlip(
                    bucket,
                    "Review the Linux port roadmap.",
                    "copy",
                    ZetlCaptureOrigin.Create(
                        "Microsoft Edge",
                        "msedge",
                        "Zetl Linux Port Roadmap",
                        ZetlCaptureOriginDetail.ApplicationAndWindowTitle));
                store.AddSlip(bucket, "Test Replay and Pop behavior.", "manual");
            }

            if (bucket.Slips.All(slip => !slip.IsImage))
            {
                store.AddImageSlip(
                    project,
                    bucket,
                    new ZetlClipboardImage(
                        Convert.FromBase64String(
                            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="),
                        1,
                        1),
                    "copy",
                    ZetlCaptureOrigin.Create(
                        "Snipping Tool",
                        "SnippingTool",
                        "Screenshot",
                        ZetlCaptureOriginDetail.ApplicationAndWindowTitle));
            }

            if (project.Buckets.All(item => item.Name != "Nested"))
            {
                store.AddBucket(project, "Nested", bucket.Id, setActive: false);
            }

            var ideasBucket = project.Buckets.FirstOrDefault(item => item.Name == "Ideas");
            if (ideasBucket is not null
                && ideasBucket.Slips.All(slip => slip.Text != "Compare formatted and plain output."))
            {
                store.AddSlip(ideasBucket, "Compare formatted and plain output.", "manual");
            }

            var nestedBucket = project.Buckets.First(item => item.Name == "Nested");
            if (nestedBucket.Slips.All(slip => slip.Text != "Verify nested bucket selection."))
            {
                store.AddSlip(nestedBucket, "Verify nested bucket selection.", "manual");
            }

            const string currentSessionPreviewText = "Current-session compile preview.";
            foreach (var slip in bucket.Slips
                .Where(slip => slip.Text == currentSessionPreviewText)
                .ToList())
            {
                store.DeleteSlip(bucket, slip.Id);
            }
            store.AddSlip(bucket, currentSessionPreviewText, "manual");

            if (store.State.Projects.All(item => item.Name != "Second Project"))
            {
                store.CreateProject(
                    "Second Project",
                    new[] { "Queue", "Scratch" },
                    "Queue",
                    shifted: true);
                store.SetActiveProject(project.Id);
            }

            var secondProject = store.State.Projects.First(item => item.Name == "Second Project");
            var queueBucket = secondProject.Buckets.First(item => item.Name == "Queue");
            if (queueBucket.Slips.All(slip => slip.Text != "Compile from another project."))
            {
                store.AddSlip(queueBucket, "Compile from another project.", "manual");
            }

            if (preview == "theme-board")
            {
                var boardWindow = new BoardWindow(
                    store,
                    templateStore: templateStore,
                    createProjectFromTemplate: CreatePreviewProject)
                {
                    Width = 820,
                    Height = 650,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Position = new PixelPoint(20, 120)
                };
                boardWindow.Opened += (_, _) =>
                {
                    var editor = new ZetlSettingsWindow(
                        settingsStore.Settings,
                        themeManager,
                        themeStore,
                        settingsStore,
                        defaultTab: "theme")
                    {
                        Width = 1080,
                        Height = 760,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Position = new PixelPoint(900, 80)
                    };
                    editor.Show(boardWindow);
                };
                desktop.MainWindow = boardWindow;
            }
            else
            {
                desktop.MainWindow = preview switch
                {
                    "project" => new ProjectSetupWindow(store.Defaults.ProjectBuckets),
                    "export" => new ProjectExportWindow(project, store.GetProjectAssets(project)),
                    "settings" => new ZetlSettingsWindow(
                        settingsStore.Settings,
                        themeManager,
                        themeStore,
                        settingsStore),
                    "bucket" => new BucketSettingsWindow(bucket, store),
                    "prompt" => new TextPromptWindow("New Bucket", "Bucket name"),
                    "first-run" => new FirstRunWindow(),
                    "notifications" => new NotificationHistoryWindow(notifications),
                    "toast" => CreateToastPreview(),
                    "board" => new BoardWindow(
                        store,
                        templateStore: templateStore,
                        createProjectFromTemplate: CreatePreviewProject),
                    "board-shift" => new BoardWindow(
                        store,
                        shiftedLane: true,
                        templateStore: templateStore,
                        createProjectFromTemplate: CreatePreviewProject),
                    "compile" => new CompileWindow(store, project),
                    "note-shortcut" => CreateShortcutNotePreview(
                        store,
                        project,
                        bucket,
                        "sample copied text"),
                    "note-image" => CreateImageNotePreview(store, project, bucket),
                    "quick-note-shortcut" => CreateShortcutNotePreview(
                        store,
                        project,
                        store.GetScratchBucket(project),
                        ""),
                    "theme" => new ZetlSettingsWindow(
                        settingsStore.Settings,
                        themeManager,
                        themeStore,
                        settingsStore,
                        defaultTab: "theme"),
                    "hold-actions" => new ZetlSettingsWindow(
                        settingsStore.Settings,
                        themeManager,
                        themeStore,
                        settingsStore,
                        defaultTab: "hold-actions"),
                    _ => new NoteCaptureWindow(store, project, bucket, "sample copied text")
                };
                AttachPreviewResult(desktop.MainWindow);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? PreviewArgument(string prefix) =>
        Program.StartupArgs
            .FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?[prefix.Length..];

    private static ZetlTemplateStore CreatePreviewTemplateStore(string stateDir)
    {
        var directory = Path.Combine(stateDir, "templates");
        var template = new ZetlTemplateDocument
        {
            Id = "preview-research",
            Name = "Research Notes",
            Category = "Preview",
            Description = "A preview-only research capture template.",
            Type = ZetlTemplateTypes.Capture,
            Buckets =
            [
                new ZetlTemplateBucketDocument
                {
                    Name = "Sources"
                },
                new ZetlTemplateBucketDocument
                {
                    Name = "Ideas"
                }
            ]
        };
        JsonFile.WriteAtomic(Path.Combine(directory, "research-notes.json"), template);
        return new ZetlTemplateStore(directory);
    }

    private static Window CreateToastPreview()
    {
        var window = new ToastWindow
        {
            // The production toast remains non-activating. The preview is the
            // application lifetime's main window, so let it activate normally.
            ShowActivated = true
        };
        var closeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            window.Close();
        };
        window.Opened += (_, _) =>
        {
            window.ShowMessage(
                "Theme saved. This is the Avalonia notification preview.",
                5000);
            closeTimer.Start();
        };
        return window;
    }

    private static NoteCaptureWindow CreateShortcutNotePreview(
        ZetlStateStore store,
        ZetlProject project,
        ZetlBucket bucket,
        string text)
    {
        return new NoteCaptureWindow(store, project, bucket, text)
        {
            DismissOnDeactivate = true
        };
    }

    private static NoteCaptureWindow CreateImageNotePreview(
        ZetlStateStore store,
        ZetlProject project,
        ZetlBucket bucket)
    {
        var note = project.Buckets
            .SelectMany(item => item.Slips)
            .First(item => item.IsImage);
        var bytes = store.ReadImageAsset(project, note)!;
        return new NoteCaptureWindow(
            store,
            project,
            bucket,
            "",
            activateByDefault: true,
            image: new ZetlClipboardImage(
                bytes,
                note.Image!.Width,
                note.Image.Height));
    }

    private static void AttachPreviewResult(Window window)
    {
        var resultPath = Program.StartupArgs
            .FirstOrDefault(arg => arg.StartsWith(
                "--preview-result=",
                StringComparison.OrdinalIgnoreCase))
            ?["--preview-result=".Length..];
        if (string.IsNullOrWhiteSpace(resultPath))
        {
            return;
        }

        window.Closed += (_, _) =>
        {
            object? result = window switch
            {
                NoteCaptureWindow note => new
                {
                    note.Saved,
                    note.ClosedByDeactivate
                },
                CompileWindow compile => new
                {
                    compile.Saved,
                    compile.ClosedByDeactivate
                },
                _ => null
            };
            if (result is null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(
                Path.GetFullPath(resultPath))!);
            File.WriteAllText(
                resultPath,
                JsonSerializer.Serialize(result, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
        };
    }
}
