using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

// Real Win32 probe, separate from the tray host: no keyboard hook, clipboard,
// profiles, or project state. The measured popup is cloaked before its first
// frame; it is closed after two animation callbacks, without invoking a menu item.
internal sealed class TrayProbeApplication : ZETL.App
{
    private TrayIcon? icon;
    private IClassicDesktopStyleApplicationLifetime desktop = null!;
    private IDisposable? openedSubscription;
    private int round;
    private long started;

    public override void OnFrameworkInitializationCompleted()
    {
        // Inherit Zetl's actual styles, but deliberately skip its normal host.
        desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        openedSubscription = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (window.Name?.StartsWith("AvaloniaTrayPopupRoot_") != true) return;
            var cloak = 1;
            if (DwmSetWindowAttribute(window.TryGetPlatformHandle()!.Handle, 13, ref cloak, sizeof(int)) != 0)
            {
                window.Close();
                Finish(1);
                throw new InvalidOperationException("Could not cloak the measurement popup.");
            }
            Console.WriteLine($"Round {round}: opened {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
            window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ =>
            {
                Console.WriteLine($"Round {round}: two frames {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
                window.Close();
                if (round >= 3) Finish(0);
                else DispatcherTimer.RunOnce(OpenMenu, TimeSpan.FromMilliseconds(300));
            }));
        });
        var menu = new NativeMenu();
        foreach (var label in new[] { "Open Board", "Open Shift Board", "New Project", "Take the Tour",
            "Measure My Taps and Holds", "Notification History", "Clear Notification History",
            "Pass-through On/Off for Now", "Settings", "-", "Open Kastn", "-", "Quit" })
            menu.Items.Add(label == "-" ? new NativeMenuItemSeparator() : new NativeMenuItem(label));
        icon = new TrayIcon { Menu = menu, ToolTipText = "Zetl tray timing probe", IsVisible = false };
        DispatcherTimer.RunOnce(() =>
        {
            if (Entry.Warmup)
            {
                var watch = Stopwatch.StartNew();
                // Diagnostics only: the helper is internal to the UI assembly.
                Assembly.Load("Zetl.UI").GetType("ZETL.ZetlTrayMenuWarmup")!
                    .GetMethod("Prepare")!.Invoke(null, [menu]);
                Console.WriteLine($"Startup layout warm-up: {watch.Elapsed.TotalMilliseconds:F1} ms");
            }
            DispatcherTimer.RunOnce(OpenMenu, TimeSpan.FromMilliseconds(300));
        }, TimeSpan.FromMilliseconds(200));
        DispatcherTimer.RunOnce(() => { Console.Error.WriteLine("Tray probe timed out."); Finish(1); }, TimeSpan.FromSeconds(15));
    }

    private void OpenMenu()
    {
        round++;
        // Exercise Avalonia 11.3.17's exact right-click path. Reflection stays in
        // this diagnostic executable; production warm-up uses only public APIs.
        var impl = typeof(TrayIcon).GetField("_impl", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(icon)!;
        var open = impl.GetType().GetMethod("OnRightClicked", BindingFlags.Instance | BindingFlags.NonPublic)!;
        started = Stopwatch.GetTimestamp();
        open.Invoke(impl, null);
        Console.WriteLine($"Round {round}: show returned {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
    }

    private void Finish(int code)
    {
        openedSubscription?.Dispose();
        icon?.Dispose();
        desktop.Shutdown(code);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
}

internal static class Entry
{
    public static bool Warmup { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("This probe requires Windows."); return 0; }
        Warmup = args.Contains("--warmup");
        return AppBuilder.Configure<TrayProbeApplication>().UsePlatformDetect()
            .With(new Win32PlatformOptions { OverlayPopups = true }).WithInterFont()
            .StartWithClassicDesktopLifetime(args);
    }
}
