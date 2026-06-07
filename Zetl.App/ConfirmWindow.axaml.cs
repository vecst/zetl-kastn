using Avalonia.Controls;

namespace ZETL;

internal partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
    }

    internal ConfirmWindow(string message, string confirmText = "Delete")
    {
        InitializeComponent();

        messageText.Text = message;
        confirmButton.Content = confirmText;
        confirmButton.Click += (_, _) => Close(true);
        cancelButton.Click += (_, _) => Close(false);
        ZetlWindowShortcuts.Enable(this, () => Close(true), () => Close(false));
    }
}
