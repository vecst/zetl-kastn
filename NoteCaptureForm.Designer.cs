namespace ZETL;

#nullable disable

partial class NoteCaptureForm
{
    private System.ComponentModel.IContainer components;
    private TableLayoutPanel layout;
    private Label bucketLabel;
    private ComboBox bucketBox;
    private FlowLayoutPanel projectModePanel;
    private CheckBox startProjectBox;
    private Label projectLabel;
    private TextBox projectNameBox;
    private FlowLayoutPanel inlineBucketPanel;
    private Label inlineBucketLabel;
    private TextBox inlineBucketNameBox;
    private CheckBox inlineInsideCurrentBox;
    private Button inlineCreateButton;
    private TextBox noteBox;
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
        bucketLabel = new Label();
        bucketBox = new ComboBox();
        projectModePanel = new FlowLayoutPanel();
        startProjectBox = new CheckBox();
        projectLabel = new Label();
        projectNameBox = new TextBox();
        inlineBucketPanel = new FlowLayoutPanel();
        inlineBucketLabel = new Label();
        inlineBucketNameBox = new TextBox();
        inlineInsideCurrentBox = new CheckBox();
        inlineCreateButton = new Button();
        noteBox = new TextBox();
        buttonsPanel = new FlowLayoutPanel();
        saveButton = new Button();
        cancelButton = new Button();

        layout.SuspendLayout();
        projectModePanel.SuspendLayout();
        inlineBucketPanel.SuspendLayout();
        buttonsPanel.SuspendLayout();
        SuspendLayout();

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(544, 331);
        FormBorderStyle = FormBorderStyle.Sizable;
        Text = "Save Zetl Note";

        layout.ColumnCount = 1;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.Controls.Add(bucketLabel, 0, 0);
        layout.Controls.Add(bucketBox, 0, 1);
        layout.Controls.Add(projectModePanel, 0, 2);
        layout.Controls.Add(inlineBucketPanel, 0, 3);
        layout.Controls.Add(noteBox, 0, 4);
        layout.Controls.Add(buttonsPanel, 0, 6);
        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(14);
        layout.RowCount = 7;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        bucketLabel.AutoSize = true;
        bucketLabel.Text = "Bucket";

        bucketBox.Dock = DockStyle.Top;
        bucketBox.DropDownStyle = ComboBoxStyle.DropDownList;

        projectModePanel.AutoSize = true;
        projectModePanel.Controls.Add(startProjectBox);
        projectModePanel.Controls.Add(projectLabel);
        projectModePanel.Controls.Add(projectNameBox);
        projectModePanel.Dock = DockStyle.Fill;

        startProjectBox.AutoSize = true;
        startProjectBox.Text = "Start project";

        projectLabel.AutoSize = true;
        projectLabel.Padding = new Padding(12, 4, 4, 0);
        projectLabel.Text = "Project";

        projectNameBox.Width = 180;

        inlineBucketPanel.AutoSize = true;
        inlineBucketPanel.Controls.Add(inlineBucketLabel);
        inlineBucketPanel.Controls.Add(inlineBucketNameBox);
        inlineBucketPanel.Controls.Add(inlineInsideCurrentBox);
        inlineBucketPanel.Controls.Add(inlineCreateButton);
        inlineBucketPanel.Dock = DockStyle.Fill;
        inlineBucketPanel.Padding = new Padding(0, 6, 0, 8);

        inlineBucketLabel.AutoSize = true;
        inlineBucketLabel.Padding = new Padding(0, 6, 4, 0);
        inlineBucketLabel.Text = "New bucket";

        inlineBucketNameBox.Width = 170;

        inlineInsideCurrentBox.AutoSize = true;

        inlineCreateButton.Text = "Create";
        inlineCreateButton.Width = 78;

        noteBox.AcceptsReturn = true;
        noteBox.Dock = DockStyle.Fill;
        noteBox.Multiline = true;
        noteBox.ScrollBars = ScrollBars.Vertical;

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
        projectModePanel.ResumeLayout(false);
        projectModePanel.PerformLayout();
        inlineBucketPanel.ResumeLayout(false);
        inlineBucketPanel.PerformLayout();
        buttonsPanel.ResumeLayout(false);
        ResumeLayout(false);
    }
}
