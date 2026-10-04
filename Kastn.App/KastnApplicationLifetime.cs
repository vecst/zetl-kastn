using ZETL;

namespace KASTN;

/// <summary>
/// Binds process control requests to one live window/session. Retires queued
/// requests and detaches pipe handlers before releasing application resources.
/// Program retains ownership of the control server and single-instance mutex.
/// </summary>
internal sealed class KastnApplicationLifetime : IAsyncDisposable
{
    private readonly KastnControlServer? control;
    private readonly Func<string?, Task> activate;
    private readonly Func<Task<bool>> decideShutdown;
    private readonly Action beginShutdown;
    private readonly Action closeConfirmed;
    private readonly Action abandonShutdown;
    private readonly Action releaseWindow;
    private readonly Action releaseThemeWatcher;
    private readonly Func<ValueTask> releaseConnection;
    private readonly Action<Action> post;
    private readonly Action<Exception> reportActivationFailure;
    private volatile bool retired;
    private bool shuttingDown;
    private Task? disposal;

    public KastnApplicationLifetime(
        KastnControlServer? control,
        Func<string?, Task> activate,
        Func<Task<bool>> decideShutdown,
        Action beginShutdown,
        Action closeConfirmed,
        Action releaseWindow,
        Action releaseThemeWatcher,
        Func<ValueTask> releaseConnection,
        Action<Action> post,
        Action<Exception> reportActivationFailure,
        Action abandonShutdown)
    {
        this.control = control;
        this.activate = activate;
        this.decideShutdown = decideShutdown;
        this.beginShutdown = beginShutdown;
        this.closeConfirmed = closeConfirmed;
        this.abandonShutdown = abandonShutdown;
        this.releaseWindow = releaseWindow;
        this.releaseThemeWatcher = releaseThemeWatcher;
        this.releaseConnection = releaseConnection;
        this.post = post;
        this.reportActivationFailure = reportActivationFailure;
        if (control is not null)
        {
            control.ActivationRequested += OnActivationRequested;
            control.ShutdownRequested = RequestShutdownAsync;
            control.ShutdownConfirmed = OnShutdownConfirmed;
            control.ShutdownAbandoned = OnShutdownAbandoned;
            if (control.PendingRequest is { } pending) RequestActivation(pending.ProjectId);
        }
    }

    private void OnActivationRequested(object? sender, KastnControlRequest request) =>
        RequestActivation(request.ProjectId);

    internal void RequestActivation(string? projectId) => post(() =>
    {
        if (!retired && !shuttingDown) _ = ActivateAsync(projectId);
    });

    private Task ActivateAsync(string? projectId) => App.ObserveActivationAsync(
        () => activate(projectId),
        ex => { if (!retired) reportActivationFailure(ex); });

    private Task<bool> RequestShutdownAsync() =>
        retired ? Task.FromResult(false) : decideShutdown();

    private void OnShutdownConfirmed() => post(() =>
    {
        if (retired || shuttingDown) return;
        StopRelaunch();
        closeConfirmed();
    });

    private void OnShutdownAbandoned() => post(() =>
    {
        if (!retired && !shuttingDown) abandonShutdown();
    });

    private void StopRelaunch()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        beginShutdown();
    }

    public ValueTask DisposeAsync()
    {
        if (disposal is not null) return new(disposal);
        retired = true;
        if (control is not null)
        {
            control.ActivationRequested -= OnActivationRequested;
            control.ShutdownRequested = null;
            control.ShutdownConfirmed = null;
            control.ShutdownAbandoned = null;
        }

        disposal = ReleaseAsync();
        return new(disposal);
    }

    private async Task ReleaseAsync()
    {
        try
        {
            StopRelaunch();
            try { releaseWindow(); }
            finally { releaseThemeWatcher(); }
        }
        finally
        {
            await releaseConnection().ConfigureAwait(false);
        }
    }
}
