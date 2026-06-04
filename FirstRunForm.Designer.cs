namespace ZETL;

#nullable disable

partial class FirstRunForm
{
    private System.ComponentModel.IContainer components;
    private TableLayoutPanel layout;
    private Label titleLabel;
    private Label bodyLabel;
    private Label coldkeysHeaderLabel;
    private TableLayoutPanel keysTable;
    private Label compileLabel;
    private Label replayLabel;
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
        keysTable = new TableLayoutPanel();
        compileLabel = new Label();
        replayLabel = new Label();
        laneLabel = new Label();
        buttonsPanel = new FlowLayoutPanel();
        gotItButton = new Button();
        openBoardButton = new Button();

        layout.SuspendLayout();
        buttonsPanel.SuspendLayout();
        SuspendLayout();

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1180, 720);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Text = "How Zetl Works";

        layout.ColumnCount = 1;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.Controls.Add(titleLabel, 0, 0);
        layout.Controls.Add(bodyLabel, 0, 1);
        layout.Controls.Add(coldkeysHeaderLabel, 0, 2);
        layout.Controls.Add(keysTable, 0, 3);
        layout.Controls.Add(compileLabel, 0, 4);
        layout.Controls.Add(replayLabel, 0, 5);
        layout.Controls.Add(laneLabel, 0, 6);
        layout.Controls.Add(buttonsPanel, 0, 7);
        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(16);
        layout.RowCount = 8;
        for (var i = 0; i < 8; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        titleLabel.AutoSize = true;
        titleLabel.Font = new Font(FontFamily.GenericSansSerif, 13F, FontStyle.Bold);
        titleLabel.MaximumSize = new Size(1140, 0);
        titleLabel.Text = "Zetl is built on Chordl.";

        bodyLabel.AutoSize = true;
        bodyLabel.MaximumSize = new Size(1140, 0);
        bodyLabel.Padding = new Padding(0, 8, 0, 10);
        bodyLabel.Text =
            "Chordl is the keyboard layer. It gives a shortcut you already know a second job: tap the chord " +
            "and it behaves exactly as normal; hold it for a moment and a second action fires too. Your usual " +
            "shortcut still happens, so nothing is lost. Zetl is the workflow built on top, filling those " +
            "holds (and a few taps) with notes, buckets, and paste tools.";

        coldkeysHeaderLabel.AutoSize = true;
        coldkeysHeaderLabel.Font = new Font(FontFamily.GenericSansSerif, 10F, FontStyle.Bold);
        coldkeysHeaderLabel.MaximumSize = new Size(1140, 0);
        coldkeysHeaderLabel.Text = "What each coldkey does. Anytime means the hold works even with no active project:";

        // Cells are built in code (BuildKeysTable).
        keysTable.AutoSize = true;
        keysTable.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        keysTable.CellBorderStyle = TableLayoutPanelCellBorderStyle.Single;
        keysTable.Margin = new Padding(0, 4, 0, 12);

        compileLabel.AutoSize = true;
        compileLabel.MaximumSize = new Size(1140, 0);
        compileLabel.Padding = new Padding(0, 0, 0, 10);
        compileLabel.Text =
            "Compile (hold Ctrl+V): gather this session's notes into one block, then copy it, paste it, or " +
            "save it into a bucket. Pick a format:\r\n" +
            "    Formatted - notes grouped under their project and bucket headings.\r\n" +
            "    Plain - just the note text, one per line, with no headings (the unformatted option).\r\n" +
            "    TSV - tab-separated rows for spreadsheets. Set the row length to say how many notes make one " +
            "row (for example 5 fields per record), and a bucket's starting-text lines can act as column headers.";

        replayLabel.AutoSize = true;
        replayLabel.MaximumSize = new Size(1140, 0);
        replayLabel.Padding = new Padding(0, 0, 0, 10);
        replayLabel.Text =
            "Replay Mode pastes a list back in the order you built it. Copy items into a bucket one by one, " +
            "switch to where they belong, and each tap of Ctrl+V drops in the next one. When the last item is " +
            "used, the bucket leaves it on the clipboard and turns Replay off, so normal paste works again.";

        laneLabel.AutoSize = true;
        laneLabel.MaximumSize = new Size(1140, 0);
        laneLabel.Padding = new Padding(0, 0, 0, 4);
        laneLabel.Text =
            "Ctrl+Shift is a second, independent lane with its own active project, so two projects can be " +
            "active at the same time. Hold Ctrl+Shift+B to open the Shift Board and mark a second project " +
            "active there. Now the two lanes work side by side: for example, keep your main project on the " +
            "normal lane so Ctrl+X saves quick notes to one specific bucket, and use Ctrl+Shift+X to drop " +
            "unrelated notes into the Shift project's Scratch — no switching the active project back and " +
            "forth. Each lane remembers its own active project, bucket, and Replay state.\r\n\r\n" +
            "Paste is the one shared piece, because there is only one system clipboard: outside Replay Mode, " +
            "Ctrl+Shift+V just pastes normally. Ctrl+Enter saves or closes any Zetl dialog, and the tray icon " +
            "has the Board, notifications, and this guide. On the Board, double-click any bucket to open its " +
            "settings: its kind, compile format, TSV headers, and starting text.";

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
