namespace ZETL;

internal sealed class ZetlToastService : IDisposable
{
    private const int MaxHistory = 300;
    private readonly List<ZetlToastEntry> history = new();
    private ZetlToastForm? toastForm;
    private ZetlToastHistoryForm? historyForm;

    public void Show(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var entry = new ZetlToastEntry(DateTime.Now, message.Trim());
        history.Add(entry);
        if (history.Count > MaxHistory)
        {
            history.RemoveRange(0, history.Count - MaxHistory);
        }

        if (toastForm is null || toastForm.IsDisposed)
        {
            toastForm = new ZetlToastForm();
        }

        toastForm.ShowMessage(entry.Message);
        historyForm?.RefreshEntries(history);
    }

    public void ShowHistory()
    {
        if (historyForm is null || historyForm.IsDisposed)
        {
            historyForm = new ZetlToastHistoryForm();
        }

        historyForm.RefreshEntries(history);
        ZetlDialogPlacement.PlaceNearTopSixth(historyForm);
        historyForm.Show();
        historyForm.WindowState = FormWindowState.Normal;
        ZetlDialogPlacement.BringToForeground(historyForm);
    }

    public void ClearHistory()
    {
        history.Clear();
        historyForm?.RefreshEntries(history);
        ShowToastOnly("Notification history cleared.");
    }

    public void Dispose()
    {
        historyForm?.Dispose();
        toastForm?.Dispose();
    }

    private void ShowToastOnly(string message)
    {
        if (toastForm is null || toastForm.IsDisposed)
        {
            toastForm = new ZetlToastForm();
        }

        toastForm.ShowMessage(message);
    }
}

internal sealed record ZetlToastEntry(DateTime CreatedAt, string Message);

internal sealed class ZetlToastForm : Form
{
    private const int DisplayMs = 950;
    private const int SW_HIDE = 0;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HwndTopMost = new IntPtr(-1);
    private readonly System.Windows.Forms.Timer dismissTimer = new();
    private string messageText = "";

    public ZetlToastForm()
    {
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = Color.FromArgb(32, 34, 38);
        ClientSize = new Size(340, 72);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        Opacity = 0.96;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;

        dismissTimer.Interval = DisplayMs;
        dismissTimer.Tick += (_, _) =>
        {
            dismissTimer.Stop();
            HideToast();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return cp;
        }
    }

    public void ShowMessage(string message)
    {
        messageText = message;
        var location = GetNotificationAreaLocation();
        _ = Handle;
        Program.SetWindowPos(Handle, HwndTopMost, location.X, location.Y, Width, Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        dismissTimer.Stop();
        dismissTimer.Start();
        Invalidate();
        Update();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var textBounds = new Rectangle(14, 10, Width - 28, Height - 20);
        TextRenderer.DrawText(
            e.Graphics,
            messageText,
            Font,
            textBounds,
            Color.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        using var borderPen = new Pen(Color.FromArgb(78, 84, 96));
        e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            dismissTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private Point GetNotificationAreaLocation()
    {
        var working = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1200, 800);
        return new Point(
            working.Right - Width - 16,
            working.Bottom - Height - 16);
    }

    private void HideToast()
    {
        if (IsHandleCreated)
        {
            Program.ShowWindow(Handle, SW_HIDE);
        }
    }
}

internal sealed class ZetlToastHistoryForm : ZetlPopupForm
{
    private readonly TableLayoutPanel layout = new();
    private readonly TextBox historyBox = new();
    private readonly FlowLayoutPanel buttonsPanel = new();
    private readonly Button closeButton = new();

    public ZetlToastHistoryForm()
    {
        Text = "Zetl Notifications";
        Width = 520;
        Height = 420;

        layout.ColumnCount = 1;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.Controls.Add(historyBox, 0, 0);
        layout.Controls.Add(buttonsPanel, 0, 1);
        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(10);
        layout.RowCount = 2;
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        historyBox.Dock = DockStyle.Fill;
        historyBox.Multiline = true;
        historyBox.ReadOnly = true;
        historyBox.ScrollBars = ScrollBars.Vertical;
        historyBox.WordWrap = true;

        buttonsPanel.AutoSize = true;
        buttonsPanel.Controls.Add(closeButton);
        buttonsPanel.Dock = DockStyle.Fill;
        buttonsPanel.FlowDirection = FlowDirection.RightToLeft;

        closeButton.Text = "Close";
        closeButton.Width = 86;
        closeButton.Click += (_, _) => Close();

        Controls.Add(layout);
    }

    public void RefreshEntries(IReadOnlyList<ZetlToastEntry> entries)
    {
        historyBox.Text = entries.Count == 0
            ? ""
            : string.Join(
                Environment.NewLine,
                entries
                    .OrderByDescending(entry => entry.CreatedAt)
                    .Select(entry => $"[{entry.CreatedAt:HH:mm:ss}] {entry.Message}"));
    }
}
