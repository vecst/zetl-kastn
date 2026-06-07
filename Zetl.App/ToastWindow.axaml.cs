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

        dismissTimer.Stop();
        dismissTimer.Start();
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
