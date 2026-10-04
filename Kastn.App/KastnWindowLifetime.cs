namespace KASTN;

/// <summary>
/// Owns close/tray decisions and the two-phase shutdown handshake. Methods run on
/// the UI thread; retirement is also visible to background snapshot publishers.
/// Native window actions and editor durability remain injected adapters.
/// </summary>
internal sealed class KastnWindowLifetime(
    Func<bool> closeToTray,
    Action show,
    Action hide,
    Func<Task<bool>> prepareEditor,
    Func<Task<bool>> confirmShutdown,
    Action close,
    Action releaseResources,
    Action<Exception> reportFailure) : IDisposable
{
    private volatile bool retired;
    private bool closing;
    private TaskCompletionSource<bool>? closeRequest;
    private TaskCompletionSource<bool>? shutdownRequest;

    public bool IsRetired => retired;
    public bool AllowClose => closing || retired;
    public bool IsBusy => closeRequest is not null || shutdownRequest is not null;

    public void Activate()
    {
        if (!retired && !closing && !IsBusy) show();
    }

    public bool CanActivate => !retired && !closing && !IsBusy;

    public Task<bool> RequestCloseAsync()
    {
        if (retired || closing || shutdownRequest is not null) return Task.FromResult(false);
        if (closeRequest is { } pending) return pending.Task;
        if (closeToTray())
        {
            hide();
            return Task.FromResult(false);
        }

        var request = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        closeRequest = request;
        _ = CompleteCloseAsync(request);
        return request.Task;
    }

    private async Task CompleteCloseAsync(TaskCompletionSource<bool> request)
    {
        try
        {
            if (await prepareEditor() && !retired && !closing)
            {
                closing = true;
                close();
                request.TrySetResult(true);
            }
        }
        catch (Exception ex)
        {
            if (!retired)
            {
                closing = false;
                reportFailure(ex);
            }
        }
        finally
        {
            request.TrySetResult(false);
            if (ReferenceEquals(closeRequest, request)) closeRequest = null;
        }
    }

    public Task<bool> DecideShutdownAsync()
    {
        if (retired || closing || closeRequest is not null) return Task.FromResult(false);
        if (shutdownRequest is { } pending) return pending.Task;
        var request = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shutdownRequest = request;
        _ = CompleteShutdownDecisionAsync(request);
        return request.Task;
    }

    private async Task CompleteShutdownDecisionAsync(TaskCompletionSource<bool> request)
    {
        var approved = false;
        try
        {
            show();
            approved = await confirmShutdown() && !retired
                && await prepareEditor() && !retired;
            // Approval holds the close gate until the control pipe flushes its
            // reply. An X click cannot tear down the server ahead of that reply.
            request.TrySetResult(approved);
        }
        catch (Exception ex)
        {
            if (!retired) reportFailure(ex);
            request.TrySetResult(false);
        }
        finally
        {
            if (!approved && ReferenceEquals(shutdownRequest, request)) shutdownRequest = null;
        }
    }

    public void CloseConfirmed()
    {
        if (retired || closing) return;
        closing = true;
        close();
    }

    public void AbandonShutdown()
    {
        if (!retired && !closing && shutdownRequest?.Task.IsCompletedSuccessfully == true)
            shutdownRequest = null;
    }

    public void Dispose()
    {
        if (retired) return;
        retired = true;
        closeRequest?.TrySetResult(closing);
        shutdownRequest?.TrySetResult(false);
        closeRequest = shutdownRequest = null;
        releaseResources();
    }
}
