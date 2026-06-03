namespace ZETL;

#nullable disable

partial class TextPromptForm
{
    private System.ComponentModel.IContainer components;
    private TableLayoutPanel layout;
    private Label promptLabel;
    private TextBox textBox;
    private FlowLayoutPanel buttonsPanel;
    private Button okButton;
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
        promptLabel = new Label();
        textBox = new TextBox();
        buttonsPanel = new FlowLayoutPanel();
        okButton = new Button();
        cancelButton = new Button();

        layout.SuspendLayout();
        buttonsPanel.SuspendLayout();
        SuspendLayout();

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(344, 111);
        FormBorderStyle = FormBorderStyle.FixedDialog;

        layout.ColumnCount = 1;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.Controls.Add(promptLabel, 0, 0);
        layout.Controls.Add(textBox, 0, 1);
        layout.Controls.Add(buttonsPanel, 0, 2);
        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(12);
        layout.RowCount = 3;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        promptLabel.AutoSize = true;

        textBox.Dock = DockStyle.Top;

        buttonsPanel.AutoSize = true;
        buttonsPanel.Controls.Add(okButton);
        buttonsPanel.Controls.Add(cancelButton);
        buttonsPanel.Dock = DockStyle.Fill;
        buttonsPanel.FlowDirection = FlowDirection.RightToLeft;

        okButton.DialogResult = DialogResult.OK;
        okButton.Text = "OK";
        okButton.Width = 80;

        cancelButton.DialogResult = DialogResult.Cancel;
        cancelButton.Text = "Cancel";
        cancelButton.Width = 80;

        AcceptButton = okButton;
        CancelButton = cancelButton;
        Controls.Add(layout);

        layout.ResumeLayout(false);
        layout.PerformLayout();
        buttonsPanel.ResumeLayout(false);
        ResumeLayout(false);
    }
}
