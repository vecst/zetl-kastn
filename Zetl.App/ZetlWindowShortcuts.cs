using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ZETL;

internal static class ZetlWindowShortcuts
{
    public static void Enable(
        Window window,
        Action accept,
        Action cancel,
        Action<KeyEventArgs>? additional = null)
    {
        // Accept/cancel run on key-up, not key-down. Closing the window on
        // key-down destroys it before the key-up is delivered, so the key-up
        // lands on whatever regains focus behind the popup -- e.g. a fullscreen
        // video that exits fullscreen on the leaked Escape. We consume the
        // closing keys on key-down (so they do nothing else and never propagate)
        // and act on key-up, while the popup still holds focus.
        var acceptArmed = false;

        window.AddHandler(
            InputElement.KeyDownEvent,
            (_, args) =>
            {
                if (args.Key == Key.Enter
                    && args.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    acceptArmed = true;
                    args.Handled = true;
                }
                else if (args.Key == Key.Escape)
                {
                    args.Handled = true;
                }
                else
                {
                    additional?.Invoke(args);
                }
            },
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        window.AddHandler(
            InputElement.KeyUpEvent,
            (_, args) =>
            {
                if (args.Key == Key.Escape)
                {
                    args.Handled = true;
                    cancel();
                }
                else if (args.Key == Key.Enter && acceptArmed)
                {
                    // Armed on the matching key-down, so a Ctrl released before
                    // Enter doesn't drop the accept.
                    acceptArmed = false;
                    args.Handled = true;
                    accept();
                }
            },
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }
}
