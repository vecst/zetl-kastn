using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ZETL;

namespace KASTN;

public partial class App : Application
{
    private KastnConnectionController? connection;
    private ZetlThemeManager? themeManager;
    private KastnThemeWatcher? themeWatcher;
    private MainWindow? mainWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var pipeName = Program.Value(Program.StartupArgs, "--ipc-pipe=");
            var zetlPath = Program.Value(Program.StartupArgs, "--zetl-path=");
            var projectId = Program.Value(Program.StartupArgs, "--project=");
            var settingsStore = new ZetlAppSettingsStore();
            // With no direct handoff, honor the "reopen last project" startup preference.
            // A since-deleted id falls through to the landing page in the connection.
            if (string.IsNullOrEmpty(projectId)
                && ZetlKastnStartup.Normalize(settingsStore.Settings.KastnStartup)
                    == ZetlKastnStartup.LastProject)
            {
                projectId = new KastnStateStore().State.LastProjectId;
            }
            var themeStore = new ZetlThemeStore();
            themeManager = new ZetlThemeManager(this);
            themeManager.Apply(
                themeStore.Resolve(settingsStore.Settings.ThemeId),
                settingsStore.Settings.ThemeVariant);
            // Re-apply live when Zetl's theme editor changes the shared settings/theme.
            themeWatcher = new KastnThemeWatcher(themeManager);
            connection = new KastnConnectionController(
                token => KastnZetlLauncher.LaunchAsync(zetlPath, pipeName, token),
                pipeName);
            mainWindow = new MainWindow(connection);
            desktop.MainWindow = mainWindow;

            if (Program.ActivationServer is { } activation)
            {
                activation.ActivationRequested += OnActivationRequested;
                // Zetl is quitting and asked whether Kastn may close too. Kastn owns
                // the decision (silent when minimized to tray, a confirm dialog when
                // open); on a "close" reply, suppress the auto-relaunch and exit.
                var window = mainWindow!;
                var session = connection!;
                activation.ShutdownRequested = () => window.RequestShutdownDecisionAsync();
                activation.ShutdownConfirmed = () => Dispatcher.UIThread.Post(() =>
                {
                    session.BeginShutdown();
                    window.CloseForShutdown();
                });
                if (activation.PendingRequest is { } pending)
                {
                    mainWindow.ActivateRequest(pending.ProjectId);
                }
            }

            desktop.Exit += (_, _) =>
            {
                if (Program.ActivationServer is { } server)
                {
                    server.ActivationRequested -= OnActivationRequested;
                }

                connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
                connection = null;
                themeWatcher?.Dispose();
                themeWatcher = null;
                themeManager = null;
                mainWindow = null;
            };
            connection.Start(projectId);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnActivationRequested(object? sender, KastnControlRequest request)
    {
        Dispatcher.UIThread.Post(() => mainWindow?.ActivateRequest(request.ProjectId));
    }
}
