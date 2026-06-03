namespace ZETL;

internal sealed partial class TextPromptForm : ZetlPopupForm
{
    public TextPromptForm(string title, string label)
    {
        InitializeComponent();
        Text = title;
        promptLabel.Text = label;

        okButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(Value))
            {
                DialogResult = DialogResult.None;
            }
        };
    }

    public string Value => textBox.Text.Trim();
}
