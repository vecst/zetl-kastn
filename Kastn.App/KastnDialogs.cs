using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace KASTN;

internal static class KastnDialogs
{
    public static async Task<string?> PromptAsync(
        Window owner,
        string title,
        string label,
        string initial = "")
    {
        var input = new TextBox { Text = initial };
        var validation = new TextBlock
        {
            Text = "Enter a value first.",
            Foreground = Avalonia.Media.Brushes.OrangeRed,
            IsVisible = false
        };
        var dialog = Dialog(title, 390, 180);
        var ok = new Button { Content = "OK", Width = 84, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                validation.IsVisible = true;
                input.Focus();
                return;
            }

            dialog.Close(input.Text.Trim());
        };
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = label },
                input,
                validation,
                Buttons(ok, cancel)
            }
        };
        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        return await dialog.ShowDialog<string?>(owner);
    }

    public static async Task<bool> ConfirmAsync(
        Window owner,
        string message,
        string confirmText)
    {
        var dialog = Dialog(confirmText, 460, 190);
        var confirm = new Button
        {
            Content = confirmText,
            Width = 110,
            IsDefault = true
        };
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                Buttons(confirm, cancel)
            }
        };
        return await dialog.ShowDialog<bool>(owner);
    }

    public static async Task MessageAsync(
        Window owner,
        string title,
        string message)
    {
        var dialog = Dialog(title, 420, 180);
        var close = new Button
        {
            Content = "Close",
            Width = 84,
            IsDefault = true,
            IsCancel = true
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                Buttons(close)
            }
        };
        await dialog.ShowDialog(owner);
    }

    private static Window Dialog(string title, double width, double height)
    {
        return new Window
        {
            Title = title,
            Width = width,
            Height = height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        foreach (var button in buttons)
        {
            panel.Children.Add(button);
        }

        return panel;
    }
}
