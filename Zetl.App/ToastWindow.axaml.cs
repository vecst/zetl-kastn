using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ZETL;

internal partial class ToastWindow : Window
{
    private readonly DispatcherTimer dismissTimer = new();

    public ToastWindow()
    {
        InitializeComponent();
        dismissTimer.Tick += (_, _) =>
        {
            dismissTimer.Stop();
            Hide();
        };
    }

    public void ShowMessage(string message, int displayMilliseconds)
    {
        messageText.Text = message;
        dismissTimer.Interval = TimeSpan.FromMilliseconds(
            Math.Max(200, displayMilliseconds));
        PositionNearNotificationArea();
        if (!IsVisible)
        {
            Show();
        }

        // Re-assert the top of the topmost band on every toast, not just the
        // first. Avalonia only issues SetWindowPos(HWND_TOPMOST) when the Topmost
        // property changes, so a toast reused after another app has claimed
        // topmost (a fullscreen video, screen-share bar, or another notification)
        // would otherwise surface behind it.
        ReassertTopmost();

        dismissTimer.Stop();
        dismissTimer.Start();
    }

    private void ReassertTopmost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // SWP_NOACTIVATE so re-topping never steals focus from the foreground app,
        // matching the toast's ShowActivated="False" contract.
        Win32Interop.SetWindowPos(
            handle,
            Win32Interop.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);
    }

    private void PositionNearNotificationArea()
    {
        var workingArea = Screens.Primary?.WorkingArea
            ?? new PixelRect(0, 0, 1200, 800);
        Position = new PixelPoint(
            workingArea.Right - (int)Width - 16,
            workingArea.Bottom - (int)Height - 16);
    }
}
