using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

// Real Win32/Skia windows, isolated state, no keyboard hook, clipboard, IPC or
// user settings. Reflection keeps this probe usable against the baseline app.
internal static class Entry
{
    internal static int Cycles;
    internal static string Mode = "both";
    internal static bool Detailed;
    internal static double Opacity = 1;
    internal static bool TrayWarmup;

    [STAThread]
    public static int Main(string[] args)
    {
        Cycles = args.Length > 0 ? int.Parse(args[0]) : 30;
        if (Cycles is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(Cycles));
        Mode = args.Length > 1 ? args[1] : "both";
        if (Mode is not ("popup" or "indicator" or "both")) throw new ArgumentException("Mode: popup, indicator or both.");
        Detailed = args.Contains("--detailed");
        TrayWarmup = args.Contains("--tray-warmup");
        if (args.Contains("--opacity=90")) Opacity = .9;
        var factory = typeof(ZETL.App).Assembly.GetType("ZETL.Program")!
            .GetMethod("CreateWindowsOptions", BindingFlags.Static | BindingFlags.NonPublic);
        var options = !args.Contains("--default-renderer") && factory is not null
            ? (Win32PlatformOptions)factory.Invoke(null, null)!
            : new Win32PlatformOptions { OverlayPopups = true };
        if (args.Contains("--software")) options.RenderingMode = [Win32RenderingMode.Software];
        var builder = AppBuilder.Configure<ProbeApp>().UsePlatformDetect().With(options).WithInterFont();
        if (args.Contains("--gpu-cache=16")) builder.With(new SkiaOptions { MaxGpuResourceSizeBytes = 16 * 1024 * 1024 });
        return builder.StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
    }
}

public sealed class ProbeApp : ZETL.App
{
    public override void OnFrameworkInitializationCompleted()
    {
        // Do not start Zetl's production host or open a preview main window.
        Dispatcher.UIThread.Post(async () =>
        {
            var exitCode = 0;
            var probeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ZetlPopupMemoryProbe"));
            var stateDirectory = Path.Combine(probeRoot, Guid.NewGuid().ToString("N"));
            try { await Run(stateDirectory); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally
            {
                var cleanupPath = Path.GetFullPath(stateDirectory);
                if (!cleanupPath.StartsWith(probeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Probe cleanup escaped its disposable directory.");
                if (Directory.Exists(cleanupPath)) Directory.Delete(cleanupPath, recursive: true);
                ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Shutdown(exitCode);
            }
        });
    }

    private static async Task Run(string stateDirectory)
    {
        var appAssembly = typeof(ZETL.App).Assembly;
        var coreAssembly = Assembly.Load("Zetl.Core");
        var storeType = coreAssembly.GetType("ZETL.ZetlStateStore")!;
        var store = storeType.GetConstructor([typeof(string), typeof(string), typeof(Action<string>)])!
            .Invoke([Path.Combine(stateDirectory, "state.json"), "memory-probe", null]);
        var project = storeType.GetMethod("CreateProject")!.Invoke(store,
            ["Memory probe", new[] { "Inbox", "Ideas" }, null, false, null, null, null, false])!;
        var bucket = storeType.GetMethod("GetScratchBucket")!.Invoke(store, [project])!;
        var popupType = appAssembly.GetType("ZETL.NoteCaptureWindow")!;
        var popupConstructor = popupType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var indicatorType = appAssembly.GetType("ZETL.ZetlHoldIndicator")!;
        var indicator = Activator.CreateInstance(indicatorType, nonPublic: true)!;
        indicatorType.GetProperty("Detailed")!.SetValue(indicator, Entry.Detailed);
        var weakWindows = new List<WeakReference>();
        var frameTimes = new List<double>();
        using var process = Process.GetCurrentProcess();
        void Sample(string label, bool collect = false)
        {
            if (collect) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            process.Refresh();
            Console.WriteLine($"{label}: working {process.WorkingSet64 / 1048576d:F1} MiB; private {process.PrivateMemorySize64 / 1048576d:F1} MiB; managed {GC.GetTotalMemory(false) / 1048576d:F1} MiB; closed windows alive {weakWindows.Count(w => w.IsAlive)} / {weakWindows.Count}; handles {process.HandleCount}; GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}; CPU {process.TotalProcessorTime.TotalMilliseconds:F0} ms.");
        }
        Console.WriteLine($"Native popup probe: {Entry.Cycles} empty open/close cycles, {Entry.Mode}; rendering enabled, no notes saved.");
        await Task.Delay(500);
        Sample("Before opening");
        TrayIcon? tray = null;
        if (Entry.TrayWarmup)
        {
            var uiAssembly = Assembly.Load("Zetl.UI");
            var themeManager = Activator.CreateInstance(uiAssembly.GetType("ZETL.ZetlThemeManager")!, [Current])!;
            var theme = coreAssembly.GetType("ZETL.ZetlThemeDefaults")!.GetMethod("CreateDusk")!.Invoke(null, null);
            themeManager.GetType().GetMethod("Apply")!.Invoke(themeManager, [theme, "System"]);
            Sample("After theme");
            var menu = new NativeMenu();
            foreach (var label in new[] { "Open Board", "Open Shift Board", "New Project", "Take the Tour",
                "Measure My Taps and Holds", "Notification History", "Clear Notification History",
                "Pass-through On/Off for Now", "Settings", "-", "Open Kastn", "-", "Quit" })
                menu.Items.Add(label == "-" ? new NativeMenuItemSeparator() : new NativeMenuItem(label));
            var icon = (WindowIcon)appAssembly.GetType("ZETL.ZetlAvaloniaHost")!
                .GetMethod("CreateTrayWindowIcon", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [0x808080u, 0x808080u])!;
            tray = new TrayIcon { Icon = icon, Menu = menu, IsVisible = false };
            Sample("After tray icon");
            uiAssembly.GetType("ZETL.ZetlTrayMenuWarmup")!.GetMethod("Prepare")!.Invoke(null, [menu]);
            Sample("After tray layout warmup");
        }
        for (var cycle = 1; cycle <= Entry.Cycles; cycle++)
        {
            if (Entry.Mode != "popup")
            {
                indicatorType.GetMethod("Start")!.Invoke(indicator,
                    ["Ctrl+X", Stopwatch.GetTimestamp(), TimeSpan.FromMilliseconds(353), "Quick note"]);
                await Task.Delay(400);
                indicatorType.GetMethod("Complete")!.Invoke(indicator, null);
                indicatorType.GetMethod("End")!.Invoke(indicator, [true]);
            }
            if (Entry.Mode != "indicator")
            {
                frameTimes.Add(await OpenAndClose(popupConstructor, store, project, bucket, weakWindows));
            }
            await Task.Delay(350);
            if (cycle is 1 or 5 or 10 or 20 || cycle == Entry.Cycles) Sample($"After {cycle}");
        }
        await Task.Delay(1000);
        Sample("Settled naturally");
        Sample("After diagnostic GC", collect: true);
        await Task.Delay(1000);
        Sample("After GC and render drain");
        if (frameTimes.Count > 1)
        {
            var warm = frameTimes.Skip(1).Order().ToArray();
            Console.WriteLine($"Popup construction through two frames: cold {frameTimes[0]:F1} ms; warm median {warm[warm.Length / 2]:F1} ms; warm max {warm[^1]:F1} ms.");
        }
        // This is a lifetime regression assertion; process memory is measured,
        // not used as a brittle cross-machine pass/fail threshold.
        if (weakWindows.Any(w => w.IsAlive)) throw new InvalidOperationException("Closed note windows remain rooted.");
        ((Window?)indicatorType.GetField("window", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(indicator))?.Close();
        tray?.Dispose();
    }

    private static async Task<double> OpenAndClose(ConstructorInfo constructor, object store, object project, object bucket, List<WeakReference> weakWindows)
    {
        var started = Stopwatch.GetTimestamp();
        var window = (Window)constructor.Invoke([store, project, bucket, "", false, false, null]);
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Opacity = Entry.Opacity;
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Show();
        window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ => rendered.TrySetResult()));
        await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        await Task.Delay(450);
        window.Close();
        weakWindows.Add(new WeakReference(window));
        return milliseconds;
    }
}
