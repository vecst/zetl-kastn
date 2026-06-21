using Avalonia;
using ZETL;

namespace KASTN;

internal static class Program
{
    private static Mutex? singleInstanceMutex;

    internal static string[] StartupArgs { get; private set; } = [];
    internal static KastnControlServer? ActivationServer { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        StartupArgs = args;
        var projectId = Value(args, "--project=");
        var activationPipe = Value(args, "--activation-pipe=");
        var mutexName = OperatingSystem.IsWindows()
            ? @"Local\KastnSingleInstance"
            : "KastnSingleInstance";
        singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            mutexName,
            out var createdNew);
        if (!createdNew)
        {
            singleInstanceMutex.Dispose();
            singleInstanceMutex = null;
            try
            {
                KastnControlChannel.ActivateAsync(projectId, activationPipe)
                    .GetAwaiter()
                    .GetResult();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Kastn is running, but activation failed: {ex.Message}");
                return 1;
            }
        }

        ActivationServer = new KastnControlServer(activationPipe);
        ActivationServer.Start();
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Kastn startup failed: {ex}");
            return 1;
        }
        finally
        {
            if (ActivationServer is not null)
            {
                ActivationServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
                ActivationServer = null;
            }

            singleInstanceMutex.ReleaseMutex();
            singleInstanceMutex.Dispose();
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

    internal static string? Value(string[] args, string prefix)
    {
        return args.FirstOrDefault(arg =>
                arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?[prefix.Length..];
    }
}
