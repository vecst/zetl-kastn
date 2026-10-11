using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ZETL;

// A message that matters enough to need an OK, such as shortcuts being off:
// a toast can vanish behind the tour before anyone reads it. A link, when
// given, opens in the browser beside the OK (the setup guide, say).
internal sealed class ZetlMessageWindow : Window
{
    public ZetlMessageWindow(string heading, string message, (string Label, Uri Target)? link = null)
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
            IsCancel = true
        };
        ok.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        if (link is { } open)
        {
            var linkButton = new Button { Content = open.Label };
            linkButton.Click += async (_, _) => await Launcher.LaunchUriAsync(open.Target);
            buttons.Children.Add(linkButton);
        }

        buttons.Children.Add(ok);
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = heading, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                buttons
            }
        };
        Opened += (_, _) => ok.Focus();
    }
}
