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
        ClientSize = new Size(660, 700);
        FormBorderStyle = FormBorderStyle.FixedDialog;
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
        titleLabel.MaximumSize = new Size(610, 0);
        titleLabel.Text = "Zetl is built on Chordl.";

        bodyLabel.AutoSize = true;
        bodyLabel.MaximumSize = new Size(610, 0);
        bodyLabel.Padding = new Padding(0, 8, 0, 10);
        bodyLabel.Text =
            "Chordl adds hold-chord keyboard interactions. Some Coldkeys need instant interaction: " +
            "copy and cut tap through first, then a hold timer starts so Zetl can modify what that command did. " +
            "Other interactions output data and wait for key-up or the held duration. Paste, for example, " +
            "happens when you release V, while holding Ctrl+V opens tools for changing how your paste works.";

        startupLabel.AutoSize = true;
        startupLabel.MaximumSize = new Size(610, 0);
        startupLabel.Padding = new Padding(0, 0, 0, 12);
        startupLabel.Text =
            "On launch, Ctrl+C and Ctrl+V behave normally when no project is active. " +
            "Held Ctrl+X can still create a quick note in Scratch without starting a full project.";

        coldkeysHeaderLabel.AutoSize = true;
        coldkeysHeaderLabel.Font = new Font(FontFamily.GenericSansSerif, 10F, FontStyle.Bold);
        coldkeysHeaderLabel.MaximumSize = new Size(610, 0);
        coldkeysHeaderLabel.Text = "Use any of these Coldkeys for Zetl actions:";

        coldkeysLabel.AutoSize = true;
        coldkeysLabel.Dock = DockStyle.Fill;
        coldkeysLabel.MaximumSize = new Size(610, 0);
        coldkeysLabel.Padding = new Padding(0, 8, 0, 8);
        coldkeysLabel.Text =
            "While a project is active:\r\n\r\n" +
            "Tap:\r\n" +
            "  Ctrl+C  - add highlighted text to the active bucket and clipboard\r\n" +
            "  Ctrl+V  - pop the last added item in Pop Mode, or consume the first FIFO item\r\n\r\n" +
            "Hold:\r\n" +
            "  Ctrl+C  - edit highlighted text before adding it to the bucket and clipboard\r\n" +
            "  Ctrl+X  - quick note, defaulting to Scratch\r\n" +
            "  Ctrl+V  - compile selected notes\r\n" +
            "  Ctrl+F  - toggle FIFO for the active bucket\r\n\r\n" +
            "Anytime, hold:\r\n" +
            "  Ctrl+B  - open the Board\r\n" +
            "  Ctrl+Z  - undo the last Zetl action\r\n\r\n" +
            "FIFO Mode:\r\n" +
            "  FIFO stands for First In, First Out. It is a structured pasting system. " +
            "If you know the order of the items you need to paste, copy them into the bucket in that order, " +
            "then switch to the target program and paste away. Zetl pastes items in the order they were copied. " +
            "When the last FIFO item is consumed, the bucket switches FIFO off and leaves that last item on the clipboard for repeated normal paste.";

        laneLabel.AutoSize = true;
        laneLabel.MaximumSize = new Size(610, 0);
        laneLabel.Padding = new Padding(0, 0, 0, 4);
        laneLabel.Text =
            "Ctrl+Shift uses a separate Shift project lane. The one exception is paste: " +
            "Ctrl+Shift+V cannot have a separate system clipboard, so outside FIFO mode it pastes the clipboard instead of a separate lane item. " +
            "Ctrl+Enter saves or closes Zetl dialogs. The tray icon also has Board, notifications, and this guide.";

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
