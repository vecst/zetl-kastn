using Avalonia.Controls;

namespace ZETL;

internal partial class NotificationHistoryWindow : Window
{
    public NotificationHistoryWindow()
        : this([])
    {
    }

    internal NotificationHistoryWindow(IReadOnlyList<ZetlNotificationEntry> entries)
    {
        InitializeComponent();
        ZetlWindowPlacement.Track(this);

        RefreshEntries(entries);
        closeButton.Click += (_, _) => Close();
        ZetlWindowShortcuts.Enable(this, Close, Close);
    }

    public void RefreshEntries(IReadOnlyList<ZetlNotificationEntry> entries)
    {
        historyBox.Text = entries.Count == 0
            ? ""
            : string.Join(
                Environment.NewLine,
                entries
                    .OrderByDescending(entry => entry.CreatedAt)
                    .Select(entry => $"[{entry.CreatedAt:HH:mm:ss}] {entry.Message}"));
        historyBox.CaretIndex = 0;
    }
}
