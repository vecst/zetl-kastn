using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;

// A two-way note window for hands-on test sessions on a remote Linux machine.
// Lines appended to <dir>/to-user.txt appear in the window (the remote
// session writes them over SSH); what the person types and sends with Enter
// is appended to <dir>/from-user.txt for the remote session to read.
// Usage: BoxChat [directory]   (default ~/zetl-chat)
internal static class Program
{
    public static string Directory { get; private set; } = "";

    [STAThread]
    public static int Main(string[] args)
    {
        Directory = args.FirstOrDefault()
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "zetl-chat");
        System.IO.Directory.CreateDirectory(Directory);
        return AppBuilder.Configure<ChatApplication>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }
}

internal sealed class ChatApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        var inbox = Path.Combine(Program.Directory, "to-user.txt");
        var outbox = Path.Combine(Program.Directory, "from-user.txt");
        var transcript = new TextBlock { FontSize = 18, TextWrapping = TextWrapping.Wrap };
        var scroller = new ScrollViewer { Content = transcript };
        var input = new TextBox
        {
            FontSize = 18,
            Watermark = "Type to Claude, then press Enter",
            AcceptsReturn = false
        };
        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(input, Dock.Bottom);
        layout.Children.Add(input);
        layout.Children.Add(scroller);
        var window = new Window
        {
            Title = "Zetl \u2194 Claude",
            Width = 640,
            Height = 420,
            Content = layout
        };

        var shown = 0L;
        void Append(string who, string text)
        {
            transcript.Text += (transcript.Text?.Length > 0 ? "\n\n" : "") + $"{who}: {text}";
            scroller.ScrollToEnd();
        }

        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(input.Text)) return;
            var line = input.Text.Trim();
            File.AppendAllText(outbox, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
            Append("You", line);
            input.Text = "";
            e.Handled = true;
        };

        // Poll the inbox; new bytes since the last read are new messages.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            if (!File.Exists(inbox)) return;
            using var stream = new FileStream(inbox, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= shown) return;
            stream.Seek(shown, SeekOrigin.Begin);
            var text = new StreamReader(stream).ReadToEnd();
            shown = stream.Length;
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Append("Claude", line.TrimEnd('\r'));
        };
        timer.Start();
        window.Opened += (_, _) => input.Focus();
        ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).MainWindow = window;
        base.OnFrameworkInitializationCompleted();
    }
}
