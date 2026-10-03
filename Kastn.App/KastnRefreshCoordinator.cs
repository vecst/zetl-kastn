namespace KASTN;

// One owner for explicit refreshes, command follow-ups, and event invalidations.
// A batch keeps commands revision-driven without fetching a full projection after
// every item. Its outer scope flushes any remaining work, including partial failure.
internal sealed class KastnRefreshCoordinator : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> refresh;
    private readonly Action<Exception> reportFailure;
    private readonly CancellationToken cancellationToken;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly KastnRefreshPump pump;
    private long requestedVersion;
    private long completedVersion;
    private int deferrals;

    public KastnRefreshCoordinator(
        Func<CancellationToken, Task> refresh,
        Action<Exception> reportFailure,
        CancellationToken cancellationToken)
    {
        this.refresh = refresh;
        this.reportFailure = reportFailure;
        this.cancellationToken = cancellationToken;
        pump = new KastnRefreshPump(RefreshPendingAsync, reportFailure, cancellationToken);
    }

    public void Request()
    {
        lock (stateGate)
        {
            requestedVersion++;
            if (deferrals == 0)
            {
                pump.Request();
            }
        }
    }

    public async Task AfterMutationAsync(CancellationToken callerCancellation = default)
    {
        lock (stateGate)
        {
            requestedVersion++;
            if (deferrals != 0)
            {
                return;
            }
        }

        await TryFlushAsync(callerCancellation).ConfigureAwait(false);
    }

    // Explicit refreshes may run inside a batch, for example its final refresh
    // after the caller has restored selection. They consume pending invalidations.
    public Task RefreshAsync(CancellationToken callerCancellation = default) =>
        RunAsync(force: true, allowDeferred: true, callerCancellation);

    public Task<bool> FlushAsync(CancellationToken callerCancellation = default) =>
        RunAsync(force: false, allowDeferred: true, callerCancellation);

    public IAsyncDisposable Defer()
    {
        lock (stateGate)
        {
            deferrals++;
        }
        return new Deferral(this);
    }

    private Task RefreshPendingAsync(CancellationToken token) => RunAsync(force: false, allowDeferred: false, token);

    private async Task<bool> RunAsync(bool force, bool allowDeferred, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
        await refreshGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            long version;
            lock (stateGate)
            {
                if (!force && ((!allowDeferred && deferrals != 0) || requestedVersion == completedVersion))
                {
                    return false;
                }
                version = requestedVersion;
            }

            await refresh(linked.Token).ConfigureAwait(false);
            lock (stateGate)
            {
                completedVersion = version;
            }
            return true;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task TryFlushAsync(CancellationToken token = default)
    {
        try
        {
            await RefreshPendingAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
        {
            // A failed projection cannot change a confirmed mutation into a
            // command failure. Retain the dirty version for the next refresh.
            reportFailure(ex);
        }
    }

    private async ValueTask EndDeferralAsync()
    {
        lock (stateGate)
        {
            deferrals--;
            if (deferrals != 0)
            {
                return;
            }
        }
        await TryFlushAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await pump.DisposeAsync().ConfigureAwait(false);
        // The controller cancels its lifetime before disposing the coordinator.
        // A barrier lets already-started foreground refreshes release the gate.
        await refreshGate.WaitAsync().ConfigureAwait(false);
        refreshGate.Release();
        refreshGate.Dispose();
    }

    private sealed class Deferral(KastnRefreshCoordinator owner) : IAsyncDisposable
    {
        private KastnRefreshCoordinator? currentOwner = owner;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref currentOwner, null)?.EndDeferralAsync() ?? ValueTask.CompletedTask;
    }
}
