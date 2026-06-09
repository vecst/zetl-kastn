using Avalonia.Controls;

namespace ZETL;

internal partial class TextPromptWindow : Window
{
    public TextPromptWindow()
        : this("New Bucket", "Bucket name")
    {
    }

    internal TextPromptWindow(string title, string label)
    {
        InitializeComponent();
        ZetlWindowPlacement.Track(this);

        Title = title;
        promptLabel.Text = label;
        okButton.Click += (_, _) => Commit();
        cancelButton.Click += (_, _) => Close();
        ZetlWindowShortcuts.Enable(this, Commit, Close);
        Opened += (_, _) => textBox.Focus();
    }

    public bool Saved { get; private set; }

    public string Value => textBox.Text?.Trim() ?? "";

    private void Commit()
    {
        if (string.IsNullOrWhiteSpace(Value))
        {
            validationText.IsVisible = true;
            textBox.Focus();
            return;
        }

        Saved = true;
        Close();
    }
}
