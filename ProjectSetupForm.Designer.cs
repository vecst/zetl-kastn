namespace ZETL;

#nullable disable

partial class ProjectSetupForm
{
    private System.ComponentModel.IContainer components;
    private TableLayoutPanel layout;
    private Label projectNameLabel;
    private TextBox projectNameBox;
    private Label bucketNamesLabel;
    private TextBox bucketNamesBox;
    private Label activeBucketLabel;
    private ComboBox activeBucketBox;
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
        projectNameLabel = new Label();
        projectNameBox = new TextBox();
        bucketNamesLabel = new Label();
        bucketNamesBox = new TextBox();
        activeBucketLabel = new Label();
        activeBucketBox = new ComboBox();
        buttonsPanel = new FlowLayoutPanel();
        saveButton = new Button();
        cancelButton = new Button();

        layout.SuspendLayout();
        buttonsPanel.SuspendLayout();
        SuspendLayout();

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(444, 371);
        ControlBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        Text = "New Zetl Project";

        layout.ColumnCount = 1;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.Controls.Add(projectNameLabel, 0, 0);
        layout.Controls.Add(projectNameBox, 0, 1);
        layout.Controls.Add(bucketNamesLabel, 0, 2);
        layout.Controls.Add(bucketNamesBox, 0, 3);
        layout.Controls.Add(activeBucketLabel, 0, 4);
        layout.Controls.Add(activeBucketBox, 0, 5);
        layout.Controls.Add(buttonsPanel, 0, 6);
        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(14);
        layout.RowCount = 7;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        projectNameLabel.AutoSize = true;
        projectNameLabel.Text = "Project name";

        projectNameBox.Width = 400;

        bucketNamesLabel.AutoSize = true;
        bucketNamesLabel.Padding = new Padding(0, 12, 0, 0);
        bucketNamesLabel.Text = "Buckets, one per line";

        bucketNamesBox.Dock = DockStyle.Fill;
        bucketNamesBox.Multiline = true;
        bucketNamesBox.ScrollBars = ScrollBars.Vertical;
        bucketNamesBox.Text = "Inbox\r\nScratch";

        activeBucketLabel.AutoSize = true;
        activeBucketLabel.Padding = new Padding(0, 12, 0, 0);
        activeBucketLabel.Text = "Active bucket";

        activeBucketBox.DropDownStyle = ComboBoxStyle.DropDownList;

        buttonsPanel.AutoSize = true;
        buttonsPanel.Controls.Add(saveButton);
        buttonsPanel.Controls.Add(cancelButton);
        buttonsPanel.Dock = DockStyle.Fill;
        buttonsPanel.FlowDirection = FlowDirection.RightToLeft;

        saveButton.DialogResult = DialogResult.OK;
        saveButton.Text = "Create";
        saveButton.Width = 86;

        cancelButton.DialogResult = DialogResult.Cancel;
        cancelButton.Text = "Cancel";
        cancelButton.Width = 86;

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Controls.Add(layout);

        layout.ResumeLayout(false);
        layout.PerformLayout();
        buttonsPanel.ResumeLayout(false);
        ResumeLayout(false);
    }
}
