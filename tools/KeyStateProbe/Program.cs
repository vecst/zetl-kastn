using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

// Logs the keys and modifiers the desktop delivers to a focused window, one line
// per event, to stdout and an optional file. Hardware checks of the Linux input
// backend use it to see the compositor's logical keyboard state: a Ctrl left
// logically down after a grab ends shows up here as Ctrl on every later key.
// Usage: KeyStateProbe [log-file]
internal static class Program
{
    public static string? LogPath { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        LogPath = args.FirstOrDefault();
        return AppBuilder.Configure<ProbeApplication>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }

    public static void Log(string line)
    {
        line = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        Console.WriteLine(line);
        if (LogPath is not null) File.AppendAllText(LogPath, line + Environment.NewLine);
    }
}

internal sealed class ProbeApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        var last = new TextBlock
        {
            FontSize = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            Text = "(no keys yet)"
        };
        var window = new Window
        {
            Title = "Zetl key probe",
            Width = 640,
            Height = 260,
            Topmost = true,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        FontSize = 22,
                        TextWrapping = TextWrapping.Wrap,
                        Text = "Zetl key probe: keep this window focused and type when asked."
                    },
                    last
                }
            }
        };

        void Show(string kind, KeyEventArgs e)
        {
            var line = $"{kind} key={e.Key} physical={e.PhysicalKey} mods={e.KeyModifiers}";
            Program.Log(line);
            last.Text = $"{kind} {e.Key}  [{e.KeyModifiers}]";
        }

        window.AddHandler(InputElement.KeyDownEvent, (_, e) => { Show("DOWN", e); e.Handled = true; },
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.KeyUpEvent, (_, e) => { Show("UP", e); e.Handled = true; },
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        window.Activated += (_, _) => Program.Log("FOCUS gained");
        window.Deactivated += (_, _) => Program.Log("FOCUS lost");
        ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).MainWindow = window;
        Program.Log("READY");
        base.OnFrameworkInitializationCompleted();
    }
}
