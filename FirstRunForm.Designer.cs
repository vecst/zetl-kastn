namespace ZETL;

#nullable disable

partial class FirstRunForm
{
    private System.ComponentModel.IContainer components;
    private TableLayoutPanel layout;
    private Label titleLabel;
    private Label bodyLabel;
    private Label coldkeysHeaderLabel;
    private Label coldkeysLabel;
    private Label startupLabel;
    private Label laneLabel;
    private FlowLayoutPanel buttonsPanel;
    private Button gotItButton;
    private Button openBoardButton;

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
        titleLabel = new Label();
        bodyLabel = new Label();
        coldkeysHeaderLabel = new Label();
        coldkeysLabel = new Label();
        startupLabel = new Label();
        laneLabel = new Label();
        buttonsPanel = new FlowLayoutPanel();
        gotItButton = new Button();
        openBoardButton = new Button();

        layout.SuspendLayout();
        buttonsPanel.SuspendLayout();
        SuspendLayout();

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(560, 440);
        ControlBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        Text = "How Zetl Works";

        layout.ColumnCount = 1;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.Controls.Add(titleLabel, 0, 0);
        layout.Controls.Add(bodyLabel, 0, 1);
        layout.Controls.Add(startupLabel, 0, 2);
        layout.Controls.Add(coldkeysHeaderLabel, 0, 3);
        layout.Controls.Add(coldkeysLabel, 0, 4);
        layout.Controls.Add(laneLabel, 0, 5);
        layout.Controls.Add(buttonsPanel, 0, 6);
        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(16);
        layout.RowCount = 7;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        titleLabel.AutoSize = true;
        titleLabel.Font = new Font(FontFamily.GenericSansSerif, 13F, FontStyle.Bold);
        titleLabel.Text = "Zetl is built on Chordl.";

        bodyLabel.AutoSize = true;
        bodyLabel.Padding = new Padding(0, 8, 0, 10);
        bodyLabel.Text =
            "Chordl is the hold-keyboard interaction: tap keeps normal Windows behavior, " +
            "hold the same chord for a second action. Zetl interprets those holds for notes, buckets, and paste workflows.";

        startupLabel.AutoSize = true;
        startupLabel.Padding = new Padding(0, 0, 0, 12);
        startupLabel.Text =
            "On launch, Ctrl+C and Ctrl+V behave normally when no project is active. " +
            "Held Ctrl+X can still create a quick note in Scratch without starting a full project.";

        coldkeysHeaderLabel.AutoSize = true;
        coldkeysHeaderLabel.Font = new Font(FontFamily.GenericSansSerif, 10F, FontStyle.Bold);
        coldkeysHeaderLabel.Text = "Hold any of these Chordl shortcuts for Zetl actions (Coldkeys):";

        coldkeysLabel.AutoSize = true;
        coldkeysLabel.Dock = DockStyle.Fill;
        coldkeysLabel.Padding = new Padding(0, 8, 0, 8);
        coldkeysLabel.Text =
            "Ctrl+C  - capture copied text or manage the project\r\n" +
            "Ctrl+X  - quick note, defaulting to Scratch\r\n" +
            "Ctrl+V  - compile selected notes\r\n" +
            "Ctrl+B  - open the Board\r\n" +
            "Ctrl+Z  - undo the last Zetl action";

        laneLabel.AutoSize = true;
        laneLabel.Padding = new Padding(0, 0, 0, 4);
        laneLabel.Text = "Ctrl+Shift uses a separate Shift project lane. The tray icon also has Board, notifications, and this guide.";

        buttonsPanel.AutoSize = true;
        buttonsPanel.Controls.Add(gotItButton);
        buttonsPanel.Controls.Add(openBoardButton);
        buttonsPanel.Dock = DockStyle.Fill;
        buttonsPanel.FlowDirection = FlowDirection.RightToLeft;
        buttonsPanel.Padding = new Padding(0, 10, 0, 0);

        gotItButton.DialogResult = DialogResult.OK;
        gotItButton.Text = "Got It";
        gotItButton.Width = 86;

        openBoardButton.Text = "Open Board";
        openBoardButton.Width = 106;

        AcceptButton = gotItButton;
        Controls.Add(layout);

        layout.ResumeLayout(false);
        layout.PerformLayout();
        buttonsPanel.ResumeLayout(false);
        ResumeLayout(false);
    }
}
