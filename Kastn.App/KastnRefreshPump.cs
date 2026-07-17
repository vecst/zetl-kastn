namespace KASTN;

/// <summary>
/// Collapses refresh invalidations into one running pass plus one dirty rerun.
/// It owns the fire-and-forget task so event handlers never create an unbounded
/// queue of refresh tasks.
/// </summary>
internal sealed class KastnRefreshPump(
    Func<CancellationToken, Task> refresh,
    Action<Exception> reportFailure,
    CancellationToken cancellationToken,
    TimeSpan? coalescingWindow = null) : IAsyncDisposable
{
    private static readonly TimeSpan DefaultCoalescingWindow = TimeSpan.FromMilliseconds(50);

    private readonly object gate = new();
    private TaskCompletionSource idle = CompletedSignal();
    private Task? runner;
    private bool dirty;
    private bool stopped;

    public void Request()
    {
        lock (gate)
        {
            if (stopped || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            dirty = true;
            if (runner is not null)
            {
                return;
            }

            idle = NewSignal();
            runner = Task.Run(RunAsync);
        }
    }

    internal Task WaitForIdleAsync()
    {
        lock (gate)
        {
            return idle.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (gate)
        {
            stopped = true;
            dirty = false;
            pending = runner;
        }

        if (pending is not null)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            lock (gate)
            {
                if (stopped || cancellationToken.IsCancellationRequested || !dirty)
                {
                    runner = null;
                    idle.TrySetResult();
                    return;
                }

                dirty = false;
            }

            try
            {
                await refresh(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                reportFailure(ex);
            }

            lock (gate)
            {
                if (stopped || cancellationToken.IsCancellationRequested || !dirty)
                {
                    runner = null;
                    idle.TrySetResult();
                    return;
                }
            }

            try
            {
                // Let adjacent notifications join the already-pending rerun. The
                // first pass remains immediate, so ordinary changes are not delayed.
                await Task.Delay(
                    coalescingWindow ?? DefaultCoalescingWindow,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (gate)
                {
                    runner = null;
                    idle.TrySetResult();
                }

                return;
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = NewSignal();
        signal.SetResult();
        return signal;
    }
}
