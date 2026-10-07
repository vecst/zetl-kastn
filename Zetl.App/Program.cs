using Avalonia;
using System.Runtime.InteropServices;

namespace ZETL;

// Entry point for the canonical cross-platform Avalonia application.
internal static class Program
{
    private static Mutex? singleInstanceMutex;

    internal static string[] StartupArgs { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        StartupArgs = args;
        if (args.Any(arg => arg.Equals("--version", StringComparison.OrdinalIgnoreCase)))
        {
            // Release scripts check that both apps identify the same commit.
            Console.WriteLine(typeof(Program).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? "unknown");
            return 0;
        }

        var ipcSmokeDirectory = args.FirstOrDefault(arg =>
                arg.StartsWith("--ipc-smoke-server=", StringComparison.OrdinalIgnoreCase))
            ?["--ipc-smoke-server=".Length..];
        if (ipcSmokeDirectory is not null)
        {
            return ZetlIpcSmokeServer.Run(args, ipcSmokeDirectory);
        }

        var parityDirectory = args.FirstOrDefault(arg =>
                arg.StartsWith("--parity-smoke=", StringComparison.OrdinalIgnoreCase))
            ?["--parity-smoke=".Length..];
        if (parityDirectory is not null)
        {
            ZetlParityScenario.Run(parityDirectory);
            return 0;
        }

        if (args.Any(arg => arg.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            return ZetlWindowsSelfTests.Run();
        }

        // --clipboard-stress=SECONDS[,THREADS[,PART]]
        if (args.FirstOrDefault(arg => arg.StartsWith("--clipboard-stress=", StringComparison.OrdinalIgnoreCase))
            is { } stress
            && stress["--clipboard-stress=".Length..].Split(',') is var stressParts
            && int.TryParse(stressParts[0], out var stressSeconds))
        {
            return ZetlWindowsSelfTests.ClipboardStress(
                stressSeconds,
                stressParts.Length > 1 && int.TryParse(stressParts[1], out var threads) ? threads : 4,
                stressParts.Length > 2 ? stressParts[2] : "all");
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
            // Render dropdowns, flyouts, and tooltips as in-window overlays
            // instead of separate top-level OS windows. Zetl's popups force
            // their own foreground/topmost (see ZetlWindowActivation); a
            // ComboBox dropdown that is its own top-level window gets caught in
            // that contest and is orphaned on screen when its owner dismisses
            // on click-away. An overlay popup is part of the owning window's
            // surface, so it can never outlive it.
            .With(CreateWindowsOptions())
            .With(new X11PlatformOptions { OverlayPopups = true })
            .WithInterFont()
            .LogToTrace();
    }

    internal static Win32PlatformOptions CreateWindowsOptions() => new()
    {
        OverlayPopups = true,
        // Zetl draws small, short-lived dialogs and an animated hold ring. A
        // dedicated ANGLE/D3D device and its caches cost much more memory than
        // those surfaces need, even after the dialogs close. Software Skia keeps
        // this tray process small; Kastn retains its separate GPU renderer.
        RenderingMode = [Win32RenderingMode.Software]
    };

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
