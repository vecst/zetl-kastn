using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ZETL;

// A message that matters enough to need an OK, such as shortcuts being off:
// a toast can vanish behind the tour before anyone reads it.
internal sealed class ZetlMessageWindow : Window
{
    public ZetlMessageWindow(string heading, string message)
    {
        Title = "Zetl";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var ok = new Button
        {
            Content = "OK",
            Width = 86,
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        ok.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = heading, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                ok
            }
        };
        Opened += (_, _) => ok.Focus();
    }
}
