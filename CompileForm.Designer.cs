namespace ZETL;

#nullable disable

partial class CompileForm
{
    private System.ComponentModel.IContainer components;
    private SplitContainer split;
    private FlowLayoutPanel sourcePanel;
    private Label sourceProjectLabel;
    private ComboBox sourceProjectBox;
    private FlowLayoutPanel selectionPanel;
    private Button selectAllButton;
    private Button selectNoneButton;
    private CheckBox sessionOnlyCheck;
    private TreeView noteTree;
    private TextBox previewBox;
    private FlowLayoutPanel compileOptionsPanel;
    private Label compileModeLabel;
    private ComboBox compileModeBox;
    private Label tsvRowLengthLabel;
    private NumericUpDown tsvRowLengthBox;
    private FlowLayoutPanel destinationPanel;
    private Label destinationLabel;
    private ComboBox destinationProjectBox;
    private ComboBox destinationBucketBox;
    private CheckBox flattenCheck;
    private FlowLayoutPanel buttonsPanel;
    private Button pasteButton;
    private Button pastePlainButton;
    private Button pasteLastButton;
    private Button copyButton;
    private Button saveBucketButton;
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
        split = new SplitContainer();
        sourcePanel = new FlowLayoutPanel();
        sourceProjectLabel = new Label();
        sourceProjectBox = new ComboBox();
        selectionPanel = new FlowLayoutPanel();
        selectAllButton = new Button();
        selectNoneButton = new Button();
        sessionOnlyCheck = new CheckBox();
        noteTree = new TreeView();
        previewBox = new TextBox();
        compileOptionsPanel = new FlowLayoutPanel();
        compileModeLabel = new Label();
        compileModeBox = new ComboBox();
        tsvRowLengthLabel = new Label();
        tsvRowLengthBox = new NumericUpDown();
        destinationPanel = new FlowLayoutPanel();
        destinationLabel = new Label();
        destinationProjectBox = new ComboBox();
        destinationBucketBox = new ComboBox();
        flattenCheck = new CheckBox();
        buttonsPanel = new FlowLayoutPanel();
        pasteButton = new Button();
        pastePlainButton = new Button();
        pasteLastButton = new Button();
        copyButton = new Button();
        saveBucketButton = new Button();
        cancelButton = new Button();

        ((System.ComponentModel.ISupportInitialize)split).BeginInit();
        split.Panel1.SuspendLayout();
        split.Panel2.SuspendLayout();
        split.SuspendLayout();
        sourcePanel.SuspendLayout();
        selectionPanel.SuspendLayout();
        compileOptionsPanel.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)tsvRowLengthBox).BeginInit();
        destinationPanel.SuspendLayout();
        buttonsPanel.SuspendLayout();
        SuspendLayout();

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(884, 481);
        Text = "Compile Zetl Buckets";

        split.Dock = DockStyle.Fill;
        split.FixedPanel = FixedPanel.Panel1;
        split.SplitterDistance = 260;
        // Fill control added first so the docked toolbar claims the top edge.
        split.Panel1.Controls.Add(noteTree);
        split.Panel1.Controls.Add(selectionPanel);
        split.Panel2.Controls.Add(previewBox);

        sourcePanel.Controls.Add(sourceProjectLabel);
        sourcePanel.Controls.Add(sourceProjectBox);
        sourcePanel.Controls.Add(sessionOnlyCheck);
        sourcePanel.Dock = DockStyle.Top;
        sourcePanel.Height = 38;
        sourcePanel.Padding = new Padding(8, 7, 8, 4);

        sourceProjectLabel.AutoSize = true;
        sourceProjectLabel.Padding = new Padding(0, 4, 4, 0);
        sourceProjectLabel.Text = "Compile from";

        sourceProjectBox.DropDownStyle = ComboBoxStyle.DropDownList;
        sourceProjectBox.DisplayMember = "Name";
        sourceProjectBox.Width = 240;

        selectionPanel.Controls.Add(selectAllButton);
        selectionPanel.Controls.Add(selectNoneButton);
        selectionPanel.Dock = DockStyle.Top;
        selectionPanel.Height = 36;
        selectionPanel.Padding = new Padding(4, 5, 4, 4);

        selectAllButton.Text = "Select All";
        selectAllButton.Width = 86;

        selectNoneButton.Text = "Select None";
        selectNoneButton.Width = 90;

        sessionOnlyCheck.AutoSize = true;
        sessionOnlyCheck.Margin = new Padding(16, 6, 0, 0);
        sessionOnlyCheck.Text = "This session only";

        noteTree.CheckBoxes = true;
        noteTree.Dock = DockStyle.Fill;
        noteTree.HideSelection = false;
        noteTree.ShowLines = true;
        noteTree.ShowRootLines = true;

        previewBox.Dock = DockStyle.Fill;
        previewBox.Multiline = true;
        previewBox.ReadOnly = true;
        previewBox.ScrollBars = ScrollBars.Both;
        previewBox.WordWrap = false;

        compileOptionsPanel.Controls.Add(compileModeLabel);
        compileOptionsPanel.Controls.Add(compileModeBox);
        compileOptionsPanel.Controls.Add(tsvRowLengthLabel);
        compileOptionsPanel.Controls.Add(tsvRowLengthBox);
        compileOptionsPanel.Dock = DockStyle.Bottom;
        compileOptionsPanel.Height = 42;
        compileOptionsPanel.Padding = new Padding(8, 7, 8, 4);

        compileModeLabel.AutoSize = true;
        compileModeLabel.Padding = new Padding(0, 4, 4, 0);
        compileModeLabel.Text = "Format";

        compileModeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        compileModeBox.Items.AddRange(new object[] { "Formatted", "Plain", "TSV" });
        compileModeBox.Width = 120;

        tsvRowLengthLabel.AutoSize = true;
        tsvRowLengthLabel.Padding = new Padding(12, 4, 4, 0);
        tsvRowLengthLabel.Text = "TSV row length";

        tsvRowLengthBox.Minimum = 1;
        tsvRowLengthBox.Maximum = 1000;
        tsvRowLengthBox.Value = 5;
        tsvRowLengthBox.Width = 70;

        destinationPanel.Controls.Add(destinationLabel);
        destinationPanel.Controls.Add(destinationProjectBox);
        destinationPanel.Controls.Add(destinationBucketBox);
        destinationPanel.Controls.Add(flattenCheck);
        destinationPanel.Dock = DockStyle.Bottom;
        destinationPanel.Height = 42;
        destinationPanel.Padding = new Padding(8, 7, 8, 4);

        destinationLabel.AutoSize = true;
        destinationLabel.Padding = new Padding(0, 4, 4, 0);
        destinationLabel.Text = "Compile to";

        destinationProjectBox.DropDownStyle = ComboBoxStyle.DropDownList;
        destinationProjectBox.DisplayMember = "Name";
        destinationProjectBox.Width = 200;
        destinationProjectBox.Margin = new Padding(0, 0, 8, 0);

        destinationBucketBox.DropDownStyle = ComboBoxStyle.DropDown;
        destinationBucketBox.Width = 220;

        flattenCheck.AutoSize = true;
        flattenCheck.Margin = new Padding(16, 6, 0, 0);
        flattenCheck.Text = "Flatten into one note";

        buttonsPanel.Controls.Add(pasteButton);
        buttonsPanel.Controls.Add(pastePlainButton);
        buttonsPanel.Controls.Add(pasteLastButton);
        buttonsPanel.Controls.Add(copyButton);
        buttonsPanel.Controls.Add(saveBucketButton);
        buttonsPanel.Controls.Add(cancelButton);
        buttonsPanel.Dock = DockStyle.Bottom;
        buttonsPanel.FlowDirection = FlowDirection.RightToLeft;
        buttonsPanel.Height = 44;
        buttonsPanel.Padding = new Padding(8);

        pasteButton.Text = "Paste Now";
        pasteButton.Width = 100;

        pastePlainButton.Text = "Paste Selected Plain";
        pastePlainButton.Width = 145;

        pasteLastButton.Text = "Paste Last Item";
        pasteLastButton.Width = 120;

        copyButton.Text = "Copy to Clipboard";
        copyButton.Width = 130;

        saveBucketButton.Text = "Save to Bucket";
        saveBucketButton.Width = 115;

        cancelButton.Text = "Cancel";
        cancelButton.Width = 86;
        cancelButton.DialogResult = DialogResult.Cancel;

        AcceptButton = copyButton;
        CancelButton = cancelButton;
        Controls.Add(split);
        Controls.Add(compileOptionsPanel);
        Controls.Add(destinationPanel);
        Controls.Add(buttonsPanel);
        // Added last so the Top-docked source bar claims the top edge above split.
        Controls.Add(sourcePanel);

        split.Panel1.ResumeLayout(false);
        split.Panel2.ResumeLayout(false);
        split.Panel2.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)split).EndInit();
        split.ResumeLayout(false);
        sourcePanel.ResumeLayout(false);
        sourcePanel.PerformLayout();
        selectionPanel.ResumeLayout(false);
        selectionPanel.PerformLayout();
        compileOptionsPanel.ResumeLayout(false);
        compileOptionsPanel.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)tsvRowLengthBox).EndInit();
        destinationPanel.ResumeLayout(false);
        destinationPanel.PerformLayout();
        buttonsPanel.ResumeLayout(false);
        ResumeLayout(false);
    }
}
