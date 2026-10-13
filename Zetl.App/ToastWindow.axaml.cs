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
        ZetlOverlayWindows.Apply(this);
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
            ZetlOverlayWindows.SettleAfterShow(this);
        }

        // Back on top on every toast, not just the first: a reused toast would
        // otherwise surface behind a fullscreen video or the app in use.
        ZetlTopmost.Reassert(this);

        dismissTimer.Stop();
        dismissTimer.Start();
    }

    private void PositionNearNotificationArea()
    {
        ZetlWindowPlacement.MoveTo(this, ZetlWindowPlacement.PlaceOnScreen(Screens.Primary, ZetlScreenAnchor.BottomRight, Width, Height));
    }
}
