using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ZETL;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // DEV HARNESS: preview the ported note-capture window against a
            // throwaway store so the Avalonia UI can be exercised and compared to
            // the WinForms version. Replaced by the tray app + real orchestration.
            var stateDir = Path.Combine(Path.GetTempPath(), "ZetlAvaloniaPreview");
            var store = new ZetlStateStore(Path.Combine(stateDir, "state.json"));
            var project = store.GetActiveProject()
                ?? store.CreateProject("Preview", new[] { "Inbox", "Ideas", "Scratch" }, "Inbox");
            desktop.MainWindow = new NoteCaptureWindow(store, project, store.GetActiveBucket(), "sample copied text");
        }

        base.OnFrameworkInitializationCompleted();
    }
}
