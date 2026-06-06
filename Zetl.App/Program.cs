using Avalonia;

namespace ZETL;

// Entry point for the cross-platform (Avalonia) Zetl head. This will become the
// single UI for both Windows and Linux; for now it stands up alongside the
// WinForms head so the Avalonia UI can be built and compared on Windows.
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
