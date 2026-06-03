namespace ZETL;

#nullable disable

partial class BoardForm
{
    private System.ComponentModel.IContainer components;
    private TableLayoutPanel rootLayout;
    private FlowLayoutPanel topPanel;
    private Label projectLabel;
    private ComboBox projectBox;
    private TextBox projectNameBox;
    private Button saveProjectButton;
    private CheckBox activeProjectBox;
    private Button newProjectButton;
    private Button deleteProjectButton;
    private Button closeBoardButton;
    private SplitContainer mainSplit;
    private TableLayoutPanel bucketLayout;
    private Label bucketsLabel;
    private ListBox bucketList;
    private FlowLayoutPanel bucketSettingsPanel;
    private Label bucketNameLabel;
    private TextBox bucketNameBox;
    private Button saveBucketButton;
    private Label bucketKindLabel;
    private ComboBox bucketKindBox;
    private CheckBox popModeBox;
    private FlowLayoutPanel bucketButtonsPanel;
    private Button addBucketButton;
    private Button deleteBucketButton;
    private TableLayoutPanel noteLayout;
    private Label notesLabel;
    private ListBox noteList;
    private Label selectedNoteLabel;
    private TextBox noteEditor;
    private FlowLayoutPanel noteButtonsPanel;
    private Button deleteNoteButton;

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
        rootLayout = new TableLayoutPanel();
        topPanel = new FlowLayoutPanel();
        projectLabel = new Label();
        projectBox = new ComboBox();
        projectNameBox = new TextBox();
        saveProjectButton = new Button();
        activeProjectBox = new CheckBox();
        newProjectButton = new Button();
        deleteProjectButton = new Button();
        closeBoardButton = new Button();
        mainSplit = new SplitContainer();
        bucketLayout = new TableLayoutPanel();
        bucketsLabel = new Label();
        bucketList = new ListBox();
        bucketSettingsPanel = new FlowLayoutPanel();
        bucketNameLabel = new Label();
        bucketNameBox = new TextBox();
        saveBucketButton = new Button();
        bucketKindLabel = new Label();
        bucketKindBox = new ComboBox();
        popModeBox = new CheckBox();
        bucketButtonsPanel = new FlowLayoutPanel();
        addBucketButton = new Button();
        deleteBucketButton = new Button();
        noteLayout = new TableLayoutPanel();
        notesLabel = new Label();
        noteList = new ListBox();
        selectedNoteLabel = new Label();
        noteEditor = new TextBox();
        noteButtonsPanel = new FlowLayoutPanel();
        deleteNoteButton = new Button();

        rootLayout.SuspendLayout();
        topPanel.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)mainSplit).BeginInit();
        mainSplit.Panel1.SuspendLayout();
        mainSplit.Panel2.SuspendLayout();
        mainSplit.SuspendLayout();
        bucketLayout.SuspendLayout();
        bucketSettingsPanel.SuspendLayout();
        bucketButtonsPanel.SuspendLayout();
        noteLayout.SuspendLayout();
        noteButtonsPanel.SuspendLayout();
        SuspendLayout();

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(940, 620);
        ControlBox = false;
        MaximizeBox = false;
        MinimumSize = new Size(760, 460);
        MinimizeBox = false;
        ShowIcon = false;

        rootLayout.ColumnCount = 1;
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        rootLayout.Controls.Add(topPanel, 0, 0);
        rootLayout.Controls.Add(mainSplit, 0, 1);
        rootLayout.Dock = DockStyle.Fill;
        rootLayout.Padding = new Padding(10);
        rootLayout.RowCount = 2;
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        topPanel.AutoSize = true;
        topPanel.Controls.Add(projectLabel);
        topPanel.Controls.Add(projectBox);
        topPanel.Controls.Add(projectNameBox);
        topPanel.Controls.Add(saveProjectButton);
        topPanel.Controls.Add(activeProjectBox);
        topPanel.Controls.Add(newProjectButton);
        topPanel.Controls.Add(deleteProjectButton);
        topPanel.Controls.Add(closeBoardButton);
        topPanel.Dock = DockStyle.Fill;

        projectLabel.AutoSize = true;
        projectLabel.Padding = new Padding(0, 7, 6, 0);
        projectLabel.Text = "Project";

        projectBox.DropDownStyle = ComboBoxStyle.DropDownList;
        projectBox.DisplayMember = "Name";
        projectBox.Width = 180;

        projectNameBox.Width = 180;

        saveProjectButton.Text = "Save Name";
        saveProjectButton.Width = 92;

        activeProjectBox.AutoSize = true;
        activeProjectBox.Text = "&Active project";

        newProjectButton.Text = "New Project";
        newProjectButton.Width = 100;

        deleteProjectButton.Text = "Delete Project";
        deleteProjectButton.Width = 108;

        closeBoardButton.Text = "Close";
        closeBoardButton.Width = 76;

        mainSplit.Dock = DockStyle.Fill;
        mainSplit.FixedPanel = FixedPanel.Panel1;
        mainSplit.SplitterDistance = 260;
        mainSplit.Panel1.Controls.Add(bucketLayout);
        mainSplit.Panel2.Controls.Add(noteLayout);

        bucketLayout.ColumnCount = 1;
        bucketLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bucketLayout.Controls.Add(bucketsLabel, 0, 0);
        bucketLayout.Controls.Add(bucketList, 0, 1);
        bucketLayout.Controls.Add(bucketSettingsPanel, 0, 2);
        bucketLayout.Controls.Add(bucketButtonsPanel, 0, 3);
        bucketLayout.Dock = DockStyle.Fill;
        bucketLayout.RowCount = 5;
        bucketLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bucketLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        bucketLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bucketLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bucketLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        bucketsLabel.AutoSize = true;
        bucketsLabel.Text = "Buckets";

        bucketList.Dock = DockStyle.Fill;

        bucketSettingsPanel.AutoSize = true;
        bucketSettingsPanel.Controls.Add(bucketNameLabel);
        bucketSettingsPanel.Controls.Add(bucketNameBox);
        bucketSettingsPanel.Controls.Add(saveBucketButton);
        bucketSettingsPanel.Controls.Add(bucketKindLabel);
        bucketSettingsPanel.Controls.Add(bucketKindBox);
        bucketSettingsPanel.Controls.Add(popModeBox);
        bucketSettingsPanel.Dock = DockStyle.Fill;

        bucketNameLabel.AutoSize = true;
        bucketNameLabel.Padding = new Padding(0, 6, 4, 0);
        bucketNameLabel.Text = "Name";

        bucketNameBox.Width = 130;

        saveBucketButton.Text = "Save Bucket";
        saveBucketButton.Width = 96;

        bucketKindLabel.AutoSize = true;
        bucketKindLabel.Padding = new Padding(0, 6, 4, 0);
        bucketKindLabel.Text = "Kind";

        bucketKindBox.DropDownStyle = ComboBoxStyle.DropDownList;
        bucketKindBox.Items.AddRange(new object[] { "Standard", "Fifo" });
        bucketKindBox.Width = 100;

        popModeBox.AutoSize = true;
        popModeBox.Text = "Pop mode";

        bucketButtonsPanel.AutoSize = true;
        bucketButtonsPanel.Controls.Add(addBucketButton);
        bucketButtonsPanel.Controls.Add(deleteBucketButton);
        bucketButtonsPanel.Dock = DockStyle.Fill;

        addBucketButton.Text = "Add Bucket";
        addBucketButton.Width = 96;

        deleteBucketButton.Text = "Delete";
        deleteBucketButton.Width = 76;

        noteLayout.ColumnCount = 1;
        noteLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        noteLayout.Controls.Add(notesLabel, 0, 0);
        noteLayout.Controls.Add(noteList, 0, 1);
        noteLayout.Controls.Add(selectedNoteLabel, 0, 2);
        noteLayout.Controls.Add(noteEditor, 0, 3);
        noteLayout.Controls.Add(noteButtonsPanel, 0, 4);
        noteLayout.Dock = DockStyle.Fill;
        noteLayout.RowCount = 5;
        noteLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        noteLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));
        noteLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        noteLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 55F));
        noteLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        notesLabel.AutoSize = true;
        notesLabel.Text = "Notes";

        noteList.Dock = DockStyle.Fill;
        noteList.DisplayMember = "Text";

        selectedNoteLabel.AutoSize = true;
        selectedNoteLabel.Padding = new Padding(0, 8, 0, 0);
        selectedNoteLabel.Text = "Selected note";

        noteEditor.Dock = DockStyle.Fill;
        noteEditor.Multiline = true;
        noteEditor.ScrollBars = ScrollBars.Vertical;

        noteButtonsPanel.AutoSize = true;
        noteButtonsPanel.Controls.Add(deleteNoteButton);
        noteButtonsPanel.Dock = DockStyle.Fill;
        noteButtonsPanel.FlowDirection = FlowDirection.RightToLeft;

        deleteNoteButton.Text = "Delete Note";
        deleteNoteButton.Width = 100;

        Controls.Add(rootLayout);

        rootLayout.ResumeLayout(false);
        rootLayout.PerformLayout();
        topPanel.ResumeLayout(false);
        topPanel.PerformLayout();
        mainSplit.Panel1.ResumeLayout(false);
        mainSplit.Panel2.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)mainSplit).EndInit();
        mainSplit.ResumeLayout(false);
        bucketLayout.ResumeLayout(false);
        bucketLayout.PerformLayout();
        bucketSettingsPanel.ResumeLayout(false);
        bucketSettingsPanel.PerformLayout();
        bucketButtonsPanel.ResumeLayout(false);
        noteLayout.ResumeLayout(false);
        noteLayout.PerformLayout();
        noteButtonsPanel.ResumeLayout(false);
        ResumeLayout(false);
    }
}
