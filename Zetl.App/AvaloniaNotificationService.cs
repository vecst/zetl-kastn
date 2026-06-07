using Avalonia.Threading;

namespace ZETL;

internal sealed class AvaloniaNotificationService : IZetlNotificationSink, IDisposable
{
    private const int MaxHistory = 300;
    private readonly ZetlActivityLogBuffer activityLog;
    private readonly List<ZetlNotificationEntry> history = [];
    private ToastWindow? toast;
    private NotificationHistoryWindow? historyWindow;

    public AvaloniaNotificationService(ZetlActivityLogBuffer activityLog)
    {
        this.activityLog = activityLog;
    }

    public int DisplayMilliseconds { get; set; } = 950;

    public IReadOnlyList<ZetlNotificationEntry> History => history;

    public void Show(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(message));
            return;
        }

        var entry = new ZetlNotificationEntry(DateTime.Now, message.Trim());
        history.Add(entry);
        if (history.Count > MaxHistory)
        {
            history.RemoveRange(0, history.Count - MaxHistory);
        }

        activityLog.Enqueue(entry.Message);
        toast ??= new ToastWindow();
        toast.ShowMessage(entry.Message, DisplayMilliseconds);
        historyWindow?.RefreshEntries(history);
    }

    public void ShowHistory()
    {
        historyWindow ??= new NotificationHistoryWindow(history);
        historyWindow.Closed += (_, _) => historyWindow = null;
        historyWindow.RefreshEntries(history);
        ZetlWindowActivation.Show(historyWindow);
    }

    public void ClearHistory()
    {
        history.Clear();
        historyWindow?.RefreshEntries(history);
        ShowTransient("Notification history cleared.");
    }

    public void Dispose()
    {
        historyWindow?.Close();
        toast?.Close();
    }

    private void ShowTransient(string message)
    {
        toast ??= new ToastWindow();
        toast.ShowMessage(message, DisplayMilliseconds);
    }
}
