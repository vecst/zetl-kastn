namespace ZETL;

internal sealed partial class FirstRunForm : ZetlPopupForm
{
    // Column pixel widths for the coldkey grid: Coldkey, Tap, Hold, Anytime.
    private static readonly int[] KeyColumnWidths = { 96, 470, 470, 96 };

    public FirstRunForm()
    {
        InitializeComponent();
        BuildKeysTable();
        openBoardButton.Click += (_, _) => OpenBoard();
        Shown += (_, _) => gotItButton.Focus();
    }

    public bool OpenBoardRequested { get; private set; }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Size the window to its content so there is no leftover gap and the
        // guide can grow or shrink with its text without manual height tuning.
        var preferredHeight = layout.PreferredSize.Height;
        if (preferredHeight > 0)
        {
            ClientSize = new Size(ClientSize.Width, preferredHeight);
        }

        ZetlDialogPlacement.PlaceNearTopSixth(this);
    }

    private void BuildKeysTable()
    {
        var rows = new (string Key, string Tap, string Hold, string Anytime)[]
        {
            ("Coldkey", "Tap", "Hold", "Anytime"),
            ("Ctrl+C", "Add the highlighted text to the active bucket and clipboard.", "Edit the text before adding it, or open the Board when nothing is copied.", ""),
            ("Ctrl+X", "Normal cut.", "Quick note, defaults to Scratch.", "Yes"),
            ("Ctrl+V", "Pop the last item (Pop Mode), or paste the next item (Replay Mode).", "Compile this session's notes.", ""),
            ("Ctrl+R", "Normal Ctrl+R.", "Turn Replay Mode on or off for the active bucket.", ""),
            ("Ctrl+B", "Normal Ctrl+B.", "Open the Board.", "Yes"),
            ("Ctrl+Z", "Normal undo.", "Zetl undo for this lane.", "Yes"),
        };

        keysTable.SuspendLayout();
        keysTable.ColumnCount = KeyColumnWidths.Length;
        keysTable.RowCount = rows.Length;
        foreach (var width in KeyColumnWidths)
        {
            keysTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        }

        for (var row = 0; row < rows.Length; row++)
        {
            keysTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var isHeader = row == 0;
            keysTable.Controls.Add(MakeCell(rows[row].Key, 0, bold: true), 0, row);
            keysTable.Controls.Add(MakeCell(rows[row].Tap, 1, bold: isHeader), 1, row);
            keysTable.Controls.Add(MakeCell(rows[row].Hold, 2, bold: isHeader), 2, row);
            keysTable.Controls.Add(MakeCell(rows[row].Anytime, 3, bold: isHeader, center: true), 3, row);
        }

        keysTable.ResumeLayout(true);
    }

    private Label MakeCell(string text, int column, bool bold = false, bool center = false)
    {
        return new Label
        {
            AutoSize = true,
            Text = text,
            Margin = new Padding(8, 6, 8, 6),
            MaximumSize = new Size(KeyColumnWidths[column] - 16, 0),
            Font = bold ? new Font(Font, FontStyle.Bold) : Font,
            TextAlign = center ? ContentAlignment.MiddleCenter : ContentAlignment.TopLeft
        };
    }

    private void OpenBoard()
    {
        OpenBoardRequested = true;
        DialogResult = DialogResult.OK;
        Close();
    }
}
