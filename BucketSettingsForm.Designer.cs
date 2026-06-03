namespace ZETL;

#nullable disable

partial class BucketSettingsForm
{
    private System.ComponentModel.IContainer components;
    private TableLayoutPanel layout;
    private Label bucketNameLabel;
    private TextBox bucketNameBox;
    private Label defaultKindLabel;
    private ComboBox defaultKindBox;
    private Label compileModeLabel;
    private ComboBox compileModeBox;
    private Label tsvRowLengthLabel;
    private NumericUpDown tsvRowLengthBox;
    private Label defaultStartingTextLabel;
    private TextBox defaultStartingTextBox;
    private FlowLayoutPanel buttonsPanel;
    private Button saveButton;
    private Button cancelButton;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (components != null)
            {
                components.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        layout = new TableLayoutPanel();
        bucketNameLabel = new Label();
        bucketNameBox = new TextBox();
        defaultKindLabel = new Label();
        defaultKindBox = new ComboBox();
        compileModeLabel = new Label();
        compileModeBox = new ComboBox();
        tsvRowLengthLabel = new Label();
        tsvRowLengthBox = new NumericUpDown();
        defaultStartingTextLabel = new Label();
        defaultStartingTextBox = new TextBox();
        buttonsPanel = new FlowLayoutPanel();
        saveButton = new Button();
        cancelButton = new Button();

        layout.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)tsvRowLengthBox).BeginInit();
        buttonsPanel.SuspendLayout();
        SuspendLayout();

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(520, 430);
        ControlBox = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        Text = "Bucket Settings";

        layout.ColumnCount = 1;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.Controls.Add(bucketNameLabel, 0, 0);
        layout.Controls.Add(bucketNameBox, 0, 1);
        layout.Controls.Add(defaultKindLabel, 0, 2);
        layout.Controls.Add(defaultKindBox, 0, 3);
        layout.Controls.Add(compileModeLabel, 0, 4);
        layout.Controls.Add(compileModeBox, 0, 5);
        layout.Controls.Add(tsvRowLengthLabel, 0, 6);
        layout.Controls.Add(tsvRowLengthBox, 0, 7);
        layout.Controls.Add(defaultStartingTextLabel, 0, 8);
        layout.Controls.Add(defaultStartingTextBox, 0, 9);
        layout.Controls.Add(buttonsPanel, 0, 10);
        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(14);
        layout.RowCount = 11;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        bucketNameLabel.AutoSize = true;
        bucketNameLabel.Text = "Bucket name";

        bucketNameBox.Dock = DockStyle.Top;

        defaultKindLabel.AutoSize = true;
        defaultKindLabel.Padding = new Padding(0, 10, 0, 0);
        defaultKindLabel.Text = "Default kind";

        defaultKindBox.DropDownStyle = ComboBoxStyle.DropDownList;
        defaultKindBox.Items.AddRange(new object[] { "Standard", "Fifo" });
        defaultKindBox.Width = 160;

        compileModeLabel.AutoSize = true;
        compileModeLabel.Padding = new Padding(0, 10, 0, 0);
        compileModeLabel.Text = "Default compile mode";

        compileModeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        compileModeBox.Items.AddRange(new object[] { "Formatted", "Plain", "TSV" });
        compileModeBox.Width = 160;

        tsvRowLengthLabel.AutoSize = true;
        tsvRowLengthLabel.Padding = new Padding(0, 10, 0, 0);
        tsvRowLengthLabel.Text = "TSV row length";

        tsvRowLengthBox.Minimum = 1;
        tsvRowLengthBox.Maximum = 1000;
        tsvRowLengthBox.Value = 5;
        tsvRowLengthBox.Width = 90;

        defaultStartingTextLabel.AutoSize = true;
        defaultStartingTextLabel.Padding = new Padding(0, 10, 0, 0);
        defaultStartingTextLabel.Text = "Default starting text / TSV headers";

        defaultStartingTextBox.AcceptsReturn = true;
        defaultStartingTextBox.Dock = DockStyle.Fill;
        defaultStartingTextBox.Multiline = true;
        defaultStartingTextBox.ScrollBars = ScrollBars.Vertical;

        buttonsPanel.AutoSize = true;
        buttonsPanel.Controls.Add(saveButton);
        buttonsPanel.Controls.Add(cancelButton);
        buttonsPanel.Dock = DockStyle.Fill;
        buttonsPanel.FlowDirection = FlowDirection.RightToLeft;

        saveButton.DialogResult = DialogResult.OK;
        saveButton.Text = "Save";
        saveButton.Width = 86;

        cancelButton.DialogResult = DialogResult.Cancel;
        cancelButton.Text = "Cancel";
        cancelButton.Width = 86;

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Controls.Add(layout);

        layout.ResumeLayout(false);
        layout.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)tsvRowLengthBox).EndInit();
        buttonsPanel.ResumeLayout(false);
        ResumeLayout(false);
    }
}
