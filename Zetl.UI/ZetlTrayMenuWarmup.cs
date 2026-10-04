using Avalonia;
using Avalonia.Controls;

namespace ZETL;

// Windows renders a NativeMenu through a MenuFlyoutPresenter. Prepare its
// templates and text layout once at startup, instead of on the first tray click.
// This window is never shown; only inert copies of the menu labels are attached.
internal static class ZetlTrayMenuWarmup
{
    public static Size Prepare(NativeMenu menu)
    {
        if (menu.Items.Count == 0) return default;
        var window = new Window
        {
            Content = CreatePresenter(menu),
            ShowInTaskbar = false,
            ShowActivated = false,
            SystemDecorations = SystemDecorations.None,
            SizeToContent = SizeToContent.WidthAndHeight
        };
        try
        {
            window.ApplyTemplate();
            window.Measure(Size.Infinity);
            window.Arrange(new Rect(window.DesiredSize));
            window.UpdateLayout();
            return window.DesiredSize;
        }
        finally { window.Close(); }
    }

    internal static MenuFlyoutPresenter CreatePresenter(NativeMenu menu) => new()
    {
        ItemsSource = menu.Items.Select(item => item is NativeMenuItemSeparator
            ? (Control)new Separator()
            : item is NativeMenuItem native ? new MenuItem
            {
                Header = native.Header,
                InputGesture = native.Gesture,
                IsEnabled = native.IsEnabled,
                ToggleType = native.ToggleType switch
                {
                    NativeMenuItemToggleType.CheckBox => MenuItemToggleType.CheckBox,
                    NativeMenuItemToggleType.Radio => MenuItemToggleType.Radio,
                    _ => MenuItemToggleType.None
                },
                IsChecked = native.IsChecked
            }
            : new Separator()).ToList()
    };
}
