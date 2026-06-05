namespace ZETL;

internal sealed class ZetlSettingsForm : ZetlPopupForm
{
    private readonly NumericUpDown toastMsBox = new();
    private readonly CheckBox autoCaptureBox = new();
    private readonly CheckBox quickNoteClipboardBox = new();
    private readonly TextBox defaultBucketsBox = new();
    private readonly ComboBox compileModeBox = new();
    private readonly NumericUpDown tsvRowLengthBox = new();
    private readonly Button saveButton = new();
    private readonly Button cancelButton = new();

    public ZetlSettingsForm(ZetlAppSettings settings)
    {
        Text = "Zetl Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(500, 380);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 7,
            Dock = DockStyle.Fill,
            Padding = new Padding(14)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var i = 0; i < 7; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        toastMsBox.Minimum = 200;
        toastMsBox.Maximum = 5000;
        toastMsBox.Increment = 50;
        toastMsBox.Value = Clamp(settings.ToastDisplayMs, 200, 5000);
        toastMsBox.Width = 90;

        autoCaptureBox.AutoSize = true;
        autoCaptureBox.Text = "Capture plain Ctrl+C into the active bucket";
        autoCaptureBox.Checked = settings.AutoCaptureOnCopy;

        quickNoteClipboardBox.AutoSize = true;
        quickNoteClipboardBox.Text = "Held Ctrl+X note also copies its text to the clipboard";
        quickNoteClipboardBox.Checked = settings.QuickNoteToClipboard;

        defaultBucketsBox.Multiline = true;
        defaultBucketsBox.AcceptsReturn = true;
        defaultBucketsBox.ScrollBars = ScrollBars.Vertical;
        defaultBucketsBox.Width = 230;
        defaultBucketsBox.Height = 92;
        defaultBucketsBox.Text = string.Join(Environment.NewLine, settings.DefaultProjectBuckets);

        compileModeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        compileModeBox.Items.AddRange(new object[] { "Formatted", "Plain", "TSV" });
        compileModeBox.SelectedItem = settings.DefaultCompileMode is "Plain" or "TSV" ? settings.DefaultCompileMode : "Formatted";
        compileModeBox.Width = 130;

        tsvRowLengthBox.Minimum = 1;
        tsvRowLengthBox.Maximum = 50;
        tsvRowLengthBox.Value = Clamp(settings.DefaultTsvRowLength, 1, 50);
        tsvRowLengthBox.Width = 90;

        layout.Controls.Add(MakeLabel("Toast display time (ms)"), 0, 0);
        layout.Controls.Add(toastMsBox, 1, 0);
        layout.Controls.Add(MakeLabel("Auto-capture on copy"), 0, 1);
        layout.Controls.Add(autoCaptureBox, 1, 1);
        layout.Controls.Add(MakeLabel("Quick note to clipboard"), 0, 2);
        layout.Controls.Add(quickNoteClipboardBox, 1, 2);
        layout.Controls.Add(MakeLabel("Default project buckets (one per line)"), 0, 3);
        layout.Controls.Add(defaultBucketsBox, 1, 3);
        layout.Controls.Add(MakeLabel("Default compile mode"), 0, 4);
        layout.Controls.Add(compileModeBox, 1, 4);
        layout.Controls.Add(MakeLabel("Default TSV row length"), 0, 5);
        layout.Controls.Add(tsvRowLengthBox, 1, 5);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 0, 0)
        };
        saveButton.Text = "Save";
        saveButton.Width = 86;
        saveButton.DialogResult = DialogResult.OK;
        cancelButton.Text = "Cancel";
        cancelButton.Width = 86;
        cancelButton.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        layout.Controls.Add(buttons, 0, 6);
        layout.SetColumnSpan(buttons, 2);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Controls.Add(layout);
    }

    public int ToastDisplayMs => (int)toastMsBox.Value;

    public bool AutoCaptureOnCopy => autoCaptureBox.Checked;

    public bool QuickNoteToClipboard => quickNoteClipboardBox.Checked;

    public List<string> DefaultProjectBuckets => defaultBucketsBox.Lines
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .ToList();

    public string DefaultCompileMode => compileModeBox.SelectedItem as string ?? "Formatted";

    public int DefaultTsvRowLength => (int)tsvRowLengthBox.Value;

    private static Label MakeLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 6, 0, 0)
        };
    }

    private static int Clamp(int value, int min, int max)
    {
        return Math.Min(max, Math.Max(min, value));
    }
}
