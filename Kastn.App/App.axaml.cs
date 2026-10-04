using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ZETL;

namespace KASTN;

public partial class App : Application
{
    private KastnApplicationLifetime? session;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Lets Zetl's keyboard hook recognise Kastn's windows, so pasting into
        // a slip editor here is an ordinary paste, never a Replay.
        ZetlWindowTag.TagAllWindows(_ => false);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var pipeName = Program.Value(Program.StartupArgs, "--ipc-pipe=");
            var zetlPath = Program.Value(Program.StartupArgs, "--zetl-path=");
            var projectId = Program.Value(Program.StartupArgs, "--project=");
            var hasDirectProjectHandoff = !string.IsNullOrEmpty(projectId);
            var draftStore = new KastnDraftStore(log: Console.Error.WriteLine);
            var settingsStore = new ZetlAppSettingsStore();
            // With no direct handoff, honor the "reopen last project" startup preference.
            // A since-deleted id falls through to the landing page in the connection.
            if (string.IsNullOrEmpty(projectId)
                && ZetlKastnStartup.Normalize(settingsStore.Settings.KastnStartup)
                    == ZetlKastnStartup.LastProject)
            {
                projectId = new KastnStateStore().State.LastProjectId;
            }
            // A crash-recovery draft takes precedence over the ordinary landing-page
            // preference so it is visible and recoverable on the next launch.
            if (!hasDirectProjectHandoff && draftStore.Draft is { } recoveryDraft)
            {
                projectId = recoveryDraft.ProjectId;
            }
            var themeStore = new ZetlThemeStore();
            var themeManager = new ZetlThemeManager(this);
            themeManager.Apply(
                themeStore.Resolve(settingsStore.Settings.ThemeId),
                settingsStore.Settings.ThemeVariant);
            // Re-apply live when Zetl's theme editor changes the shared settings/theme.
            var themeWatcher = new KastnThemeWatcher(themeManager);
            var connection = new KastnConnectionController(
                token => KastnZetlLauncher.LaunchAsync(zetlPath, pipeName, token),
                pipeName);
            var mainWindow = new MainWindow(connection, draftStore);
            desktop.MainWindow = mainWindow;
            session = new KastnApplicationLifetime(
                Program.ActivationServer,
                mainWindow.ActivateRequestAsync,
                mainWindow.RequestShutdownDecisionAsync,
                connection.BeginShutdown,
                mainWindow.CloseForShutdown,
                mainWindow.RetireLifetime,
                themeWatcher.Dispose,
                connection.DisposeAsync,
                action => Dispatcher.UIThread.Post(action),
                ex =>
                {
                    Console.Error.WriteLine($"Kastn activation failed ({ex.GetType().Name}): {ex.Message}");
                    mainWindow.ReportActivationFailure(ex);
                },
                mainWindow.AbandonShutdown);

            desktop.Exit += (_, _) =>
            {
                session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                session = null;
            };
            connection.Start(projectId);
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static async Task ObserveActivationAsync(
        Func<Task> activate,
        Action<Exception> reportFailure)
    {
        try
        {
            await activate();
        }
        catch (Exception ex)
        {
            reportFailure(ex);
        }
    }
}
