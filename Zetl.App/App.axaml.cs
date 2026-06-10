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

            // DEV HARNESS: preview ported windows against throwaway data.
            // Each instance gets its own state directory so concurrent
            // previews never contend on the same files.
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
            var themeManager = new ZetlThemeManager(this, settingsStore);
            themeManager.Apply(
                themeStore.Resolve(themeIdArgument ?? settingsStore.Settings.ThemeId),
                themeArgument is "light" or "dark"
                    ? themeArgument
                    : settingsStore.Settings.ThemeVariant,
                persist: false);
            var store = new ZetlStateStore(Path.Combine(stateDir, "state.json"));
            var project = store.GetActiveProject()
                ?? store.CreateProject("Preview", new[] { "Inbox", "Ideas", "Scratch" }, "Inbox");
            var bucket = store.GetActiveBucket()
                ?? project.Buckets.First();
            bucket.DefaultKind = "Replay";
            bucket.DefaultCompileMode = "TSV";
            bucket.DefaultStartingText = "Name\nEmail\nStatus";
            bucket.DefaultTsvRowLength = 3;
            var notifications = new[]
            {
                new ZetlNotificationEntry(DateTime.Now.AddMinutes(-8), "Captured to Inbox in Preview."),
                new ZetlNotificationEntry(DateTime.Now.AddMinutes(-3), "Queue replay is on."),
                new ZetlNotificationEntry(DateTime.Now, "Compiled to Scratch in Preview.")
            };
            if (bucket.Notes.Count == 0)
            {
                store.AddNote(bucket, "Review the Linux port roadmap.", "manual");
                store.AddNote(bucket, "Test Replay and Pop behavior.", "manual");
            }

            if (project.Buckets.All(item => item.Name != "Nested"))
            {
                store.AddBucket(project, "Nested", bucket.Id, setActive: false);
            }

            var ideasBucket = project.Buckets.FirstOrDefault(item => item.Name == "Ideas");
            if (ideasBucket is not null
                && ideasBucket.Notes.All(note => note.Text != "Compare formatted and plain output."))
            {
                store.AddNote(ideasBucket, "Compare formatted and plain output.", "manual");
            }

            var nestedBucket = project.Buckets.First(item => item.Name == "Nested");
            if (nestedBucket.Notes.All(note => note.Text != "Verify nested bucket selection."))
            {
                store.AddNote(nestedBucket, "Verify nested bucket selection.", "manual");
            }

            const string currentSessionPreviewText = "Current-session compile preview.";
            foreach (var note in bucket.Notes
                .Where(note => note.Text == currentSessionPreviewText)
                .ToList())
            {
                store.DeleteNote(bucket, note.Id);
            }
            store.AddNote(bucket, currentSessionPreviewText, "manual");

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
            if (queueBucket.Notes.All(note => note.Text != "Compile from another project."))
            {
                store.AddNote(queueBucket, "Compile from another project.", "manual");
            }

            if (preview == "theme-board")
            {
                var boardWindow = new BoardWindow(store)
                {
                    Width = 820,
                    Height = 650,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Position = new PixelPoint(20, 120)
                };
                boardWindow.Opened += (_, _) =>
                {
                    var editor = new ThemeEditorWindow(
                        themeManager,
                        themeStore,
                        settingsStore)
                    {
                        Width = 900,
                        Height = 700,
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
                    "board" => new BoardWindow(store),
                    "board-shift" => new BoardWindow(store, shiftedLane: true),
                    "compile" => new CompileWindow(store, project),
                    "note-shortcut" => CreateShortcutNotePreview(
                        store,
                        project,
                        bucket,
                        "sample copied text"),
                    "quick-note-shortcut" => CreateShortcutNotePreview(
                        store,
                        project,
                        store.GetScratchBucket(project),
                        ""),
                    "theme" => new ThemeEditorWindow(
                        themeManager,
                        themeStore,
                        settingsStore),
                    _ => new NoteCaptureWindow(store, project, bucket, "sample copied text")
                };
                AttachPreviewResult(desktop.MainWindow);
            }
        }

        base.OnFrameworkInitializationCompleted();
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
