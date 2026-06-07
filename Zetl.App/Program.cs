using Avalonia;
using System.Runtime.InteropServices;

namespace ZETL;

// Entry point for the cross-platform (Avalonia) Zetl head. This will become the
// single UI for both Windows and Linux; for now it stands up alongside the
// WinForms head so the Avalonia UI can be built and compared on Windows.
internal static class Program
{
    private static Mutex? singleInstanceMutex;

    internal static string[] StartupArgs { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        StartupArgs = args;
        var parityDirectory = args.FirstOrDefault(arg =>
                arg.StartsWith("--parity-smoke=", StringComparison.OrdinalIgnoreCase))
            ?["--parity-smoke=".Length..];
        if (parityDirectory is not null)
        {
            ZetlParityScenario.Run(parityDirectory);
            return 0;
        }

        var preview = args.Any(arg =>
            arg.StartsWith("--preview=", StringComparison.OrdinalIgnoreCase));
        var ownsMutex = false;
        if (!preview)
        {
            var mutexName = OperatingSystem.IsWindows()
                ? @"Local\ZetlSingleInstance"
                : "ZetlSingleInstance";
            singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                mutexName,
                out var createdNew);
            if (!createdNew)
            {
                singleInstanceMutex.Dispose();
                singleInstanceMutex = null;
                ShowStartupMessage(
                    "Zetl is already running in this session.",
                    "Zetl");
                return 0;
            }

            ownsMutex = true;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            ShowStartupMessage(ex.Message, "Zetl startup failed");
            return 1;
        }
        finally
        {
            if (ownsMutex)
            {
                singleInstanceMutex?.ReleaseMutex();
            }

            singleInstanceMutex?.Dispose();
            singleInstanceMutex = null;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }

    private static void ShowStartupMessage(string message, string title)
    {
        if (OperatingSystem.IsWindows())
        {
            MessageBox(IntPtr.Zero, message, title, 0x00000010);
            return;
        }

        Console.Error.WriteLine($"{title}: {message}");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(
        IntPtr window,
        string text,
        string caption,
        uint type);
}
