using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ZETL;

/// <summary>One entry of Zetl's menu; a null label is a separator.</summary>
internal sealed record ZetlMenuCommand(string? Label, Action? Run)
{
    public static ZetlMenuCommand Separator { get; } = new(null, null);
}

// Zetl's tray menu as a window, for opening Zetl again while it runs (from the
// app menu, say) and for desktops that show no tray icons, like stock GNOME.
// Same commands as the tray. Choosing one closes the window first, so the
// command's own window takes the foreground; Esc or a click elsewhere closes it.
internal sealed class ZetlMenuWindow : ZetlPopupWindow
{
    private readonly StackPanel items;

    public ZetlMenuWindow(IReadOnlyList<ZetlMenuCommand> commands)
    {
        Title = "Zetl";
        Width = 280;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        SystemDecorations = SystemDecorations.BorderOnly;
        ShowInTaskbar = false;

        items = new StackPanel { Spacing = 2 };
        foreach (var command in commands)
        {
            if (command.Label is null || command.Run is null)
            {
                items.Children.Add(new Separator { Margin = new Thickness(0, 4) });
                continue;
            }

            var button = new Button
            {
                Content = command.Label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 6)
            };
            button.Click += (_, _) =>
            {
                Close();
                command.Run();
            };
            items.Children.Add(button);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(10),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Zetl is running", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(4, 0) },
                items
            }
        };
        ZetlWindowShortcuts.Enable(this, accept: () => { }, cancel: Close);
    }

    // The first command takes the keyboard, so arrow keys and Enter work at once.
    internal override Avalonia.Input.IInputElement? InitialFocusElement =>
        items.Children.OfType<Button>().FirstOrDefault();

    protected override bool IsDismissSuppressed => false;

    protected override void OnClickAwayDismiss() => Close();
}
