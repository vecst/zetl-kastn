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
        window.AddHandler(
            InputElement.KeyDownEvent,
            (_, args) =>
            {
                if (args.Key == Key.Enter
                    && args.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    args.Handled = true;
                    accept();
                }
                else if (args.Key == Key.Escape)
                {
                    args.Handled = true;
                    cancel();
                }
                else
                {
                    additional?.Invoke(args);
                }
            },
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }
}
