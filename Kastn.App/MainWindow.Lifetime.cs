using Avalonia.Controls;
using Avalonia.Threading;

namespace KASTN;

internal partial class MainWindow
{
    private KastnWindowLifetime CreateWindowLifetime() => new(
        () => CurrentAppSettings().KastnCloseToTray,
        ShowForActivation,
        HideToTray,
        () => PrepareEditorForExitAsync(),
        () => KastnDialogs.ConfirmAsync(this,
            "Closing Zetl will also close Kastn. Close both apps?", "Close both"),
        Close,
        ReleaseWindowResources,
        ex =>
        {
            Console.Error.WriteLine($"Kastn close failed ({ex.GetType().Name}): {ex.Message}");
            statusText.Text = "Kastn could not finish closing. Your editor remains open.";
        });

    private void WireWindowLifetime()
    {
        Closing += (_, args) =>
        {
            if (lifetime.AllowClose) return;
            args.Cancel = true;
            _ = lifetime.RequestCloseAsync();
        };
        Closed += (_, _) => lifetime.Dispose();
    }

    private void ShowForActivation()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        Show();
        Activate();
        BringToForeground();
    }

    private void HideToTray()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        ShowInTaskbar = false;
        Hide();
    }

    // The control pipe calls off-thread; only the adapter dispatches to Avalonia.
    public Task<bool> RequestShutdownDecisionAsync()
    {
        if (lifetime.IsRetired) return Task.FromResult(false);
        var decided = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => _ = RelayDecisionAsync(lifetime.DecideShutdownAsync(), decided));
        return decided.Task;
    }

    private static async Task RelayDecisionAsync(Task<bool> decision, TaskCompletionSource<bool> completion)
    {
        try { completion.TrySetResult(await decision.ConfigureAwait(false)); }
        catch (Exception ex) { completion.TrySetException(ex); }
    }

    public void CloseForShutdown() => Dispatcher.UIThread.Post(lifetime.CloseConfirmed);
    internal void AbandonShutdown() => Dispatcher.UIThread.Post(lifetime.AbandonShutdown);
    internal void RetireLifetime() => lifetime.Dispose();

    private void ReleaseWindowResources()
    {
        if (connection is not null) connection.SnapshotChanged -= OnSnapshotChanged;
        editHistory.Retire();
        draftJournalTimer?.Stop();
        savingVisualTimer?.Stop();
        dragScrollTimer?.Stop();
        boardDragScrollTimer?.Stop();
        ApplyDropMarker(null);
        readerPresenter.Dispose();
        boardPresenter.Dispose();
        viewEditor.Close();
        pictureCache.Dispose();
    }
}
