using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace KASTN;

public partial class App : Application
{
    private KastnConnectionController? connection;
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
            connection = new KastnConnectionController(
                token => KastnZetlLauncher.LaunchAsync(zetlPath, pipeName, token),
                pipeName);
            mainWindow = new MainWindow(connection);
            desktop.MainWindow = mainWindow;

            if (Program.ActivationServer is { } activation)
            {
                activation.ActivationRequested += OnActivationRequested;
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
                mainWindow = null;
            };
            connection.Start(projectId);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnActivationRequested(object? sender, KastnActivationRequest request)
    {
        Dispatcher.UIThread.Post(() => mainWindow?.ActivateRequest(request.ProjectId));
    }
}
