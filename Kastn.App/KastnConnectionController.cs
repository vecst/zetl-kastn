using System.Text.Json;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal sealed class KastnConnectionController : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> launchZetl;
    private readonly string? pipeName;
    private readonly TimeSpan connectTimeout;
    private readonly TimeSpan retryDelay;
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly object stateGate = new();
    private ZetlIpcClient? client;
    private Task? runTask;
    private string? desiredProjectId;
    private bool projectSelectionRequested = true;
    private bool hasEverConnected;
    private bool launchAttemptedForOutage;
    private volatile bool suppressRelaunch;

    public KastnConnectionController(
        Func<CancellationToken, Task> launchZetl,
        string? pipeName = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? retryDelay = null)
    {
        this.launchZetl = launchZetl;
        this.pipeName = pipeName;
        this.connectTimeout = connectTimeout ?? TimeSpan.FromMilliseconds(750);
        this.retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
        Current = new KastnSessionSnapshot(
            KastnConnectionState.Connecting,
            "Connecting to Zetl...",
            [],
            null);
    }

    public event EventHandler<KastnSessionSnapshot>? SnapshotChanged;

    public KastnSessionSnapshot Current { get; private set; }

    public void Start(string? projectId = null)
    {
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            desiredProjectId = projectId;
            projectSelectionRequested = false;
        }

        runTask ??= Task.Run(() => RunAsync(cancellation.Token));
    }

    /// <summary>
    /// Marks this session as shutting down on purpose (Zetl asked Kastn to close
    /// with it). Stops the reconnect loop from relaunching Zetl as it tears down.
    /// </summary>
    public void BeginShutdown()
    {
        suppressRelaunch = true;
        cancellation.Cancel();
    }

    public async Task NavigateToProjectAsync(
        string? projectId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            desiredProjectId = null;
            projectSelectionRequested = true;
        }
        else
        {
            desiredProjectId = projectId;
            projectSelectionRequested = false;
        }

        var connected = client;
        if (connected is not null && connected.IsConnected)
        {
            await RefreshAsync(connected, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var connected = client;
        if (connected is not null && connected.IsConnected)
        {
            await RefreshAsync(connected, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ZetlResponseEnvelope> ExecuteAsync(
        ZetlCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        var connected = client;
        if (connected is null || !connected.IsConnected)
        {
            throw new InvalidOperationException("Zetl is offline.");
        }

        var response = await connected.ExecuteAsync(command, cancellationToken)
            .ConfigureAwait(false);
        if (response.Status == ZetlResponseStatus.Success)
        {
            await RefreshAsync(connected, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    public async Task<ZetlResponseEnvelope> QueryAsync(
        ZetlCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        var connected = client;
        if (connected is null || !connected.IsConnected)
        {
            throw new InvalidOperationException("Zetl is offline.");
        }

        return await connected.ExecuteAsync(command, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        refreshGate.Dispose();
        cancellation.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Publish(
                KastnConnectionState.Connecting,
                hasEverConnected || launchAttemptedForOutage
                    ? "Reconnecting to Zetl..."
                    : "Connecting to Zetl...");

            var nextClient = new ZetlIpcClient("Kastn", pipeName);
            try
            {
                await nextClient.ConnectAsync(connectTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                await nextClient.DisposeAsync().ConfigureAwait(false);
                var serverAbsent =
                    ex is IOException or TimeoutException or OperationCanceledException;
                if (!suppressRelaunch && !launchAttemptedForOutage && serverAbsent)
                {
                    launchAttemptedForOutage = true;
                    try
                    {
                        await launchZetl(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception launchError)
                    {
                        Publish(
                            KastnConnectionState.Offline,
                            $"Zetl is unavailable. {launchError.Message}");
                    }
                }
                else
                {
                    Publish(
                        KastnConnectionState.Offline,
                        serverAbsent
                            ? "Zetl is offline. Kastn will keep trying."
                            : $"Kastn could not connect to Zetl. {ex.Message}");
                }

                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            client = nextClient;
            hasEverConnected = true;
            launchAttemptedForOutage = false;
            var disconnected = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            nextClient.Disconnected += (_, _) => disconnected.TrySetResult();
            nextClient.ProjectChanged += OnProjectChanged;

            try
            {
                await RefreshAsync(nextClient, cancellationToken).ConfigureAwait(false);
                await disconnected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested
                && ex is IOException or InvalidDataException or InvalidOperationException)
            {
                Publish(
                    KastnConnectionState.Offline,
                    $"The Zetl connection failed. {ex.Message}");
            }
            finally
            {
                nextClient.ProjectChanged -= OnProjectChanged;
                if (ReferenceEquals(client, nextClient))
                {
                    client = null;
                }

                await nextClient.DisposeAsync().ConfigureAwait(false);
            }

            Publish(
                KastnConnectionState.Offline,
                "Zetl disconnected. Kastn will reconnect automatically.");
            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnProjectChanged(object? _sender, ZetlProjectChangedEvent _change)
    {
        _ = RefreshAfterChangeAsync();
    }

    private async Task RefreshAfterChangeAsync()
    {
        try
        {
            await RefreshAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
        }
    }

    private async Task RefreshAsync(
        ZetlIpcClient connected,
        CancellationToken cancellationToken)
    {
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(client, connected) || !connected.IsConnected)
            {
                return;
            }

            var listResponse = await connected.ExecuteAsync(
                new ZetlCommandEnvelope
                {
                    CommandId = Guid.NewGuid().ToString("N"),
                    Kind = ZetlCommandKind.ListProjects
                },
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(listResponse);
            var projects = listResponse.Payload?.Deserialize<List<ZetlProjectSummary>>(
                    ZetlProtocolJson.Options)
                ?? [];

            var projectId = projectSelectionRequested ? null : desiredProjectId;
            if (projectId is not null && projects.All(project => project.Id != projectId))
            {
                projectId = Current.Project is { } current
                    && projects.Any(project => project.Id == current.Id)
                        ? current.Id
                        : null;
                if (projectId is null)
                {
                    projectSelectionRequested = true;
                }
            }

            ZetlProjectSnapshot? projectSnapshot = null;
            if (projectId is not null)
            {
                // The summary carries the open project's change sequence, so when a
                // redundant refresh lands (a command's own follow-up, the matching
                // change event, and the caller's explicit refresh can all arrive for
                // one action) the full snapshot fetch is skipped and the held one
                // reused. Publishing stays unconditional: a subscriber that
                // suppressed earlier applies (batching) must still get a final one.
                if (Current is { ConnectionState: KastnConnectionState.Online, Project: { } openProject }
                    && string.Equals(projectId, openProject.Id, StringComparison.Ordinal)
                    && projects.FirstOrDefault(summary => summary.Id == projectId) is { } openSummary
                    && openSummary.ChangeSequence == openProject.ChangeSequence
                    && openSummary.MetadataRevision == openProject.MetadataRevision)
                {
                    projectSnapshot = openProject;
                    desiredProjectId = openProject.Id;
                    projectSelectionRequested = false;
                }
                else
                {
                    var projectResponse = await connected.ExecuteAsync(
                        new ZetlCommandEnvelope
                        {
                            CommandId = Guid.NewGuid().ToString("N"),
                            Kind = ZetlCommandKind.GetProject,
                            ProjectId = projectId
                        },
                        cancellationToken).ConfigureAwait(false);
                    EnsureSuccess(projectResponse);
                    projectSnapshot = projectResponse.Payload?.Deserialize<ZetlProjectSnapshot>(
                        ZetlProtocolJson.Options);
                    desiredProjectId = projectSnapshot?.Id;
                    projectSelectionRequested = projectSnapshot is null;
                }
            }

            Publish(new KastnSessionSnapshot(
                KastnConnectionState.Online,
                projectSnapshot is null
                    ? projects.Count == 0
                        ? "Connected to Zetl. No projects yet."
                        : "Connected to Zetl. Select a project."
                    : $"Connected to Zetl. Viewing {projectSnapshot.Name}.",
                projects,
                projectSnapshot));
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private static void EnsureSuccess(ZetlResponseEnvelope response)
    {
        if (response.Status != ZetlResponseStatus.Success)
        {
            throw new InvalidOperationException(
                response.Error?.Message ?? $"Zetl returned {response.Status}.");
        }
    }

    private void Publish(KastnConnectionState state, string status)
    {
        Publish(Current with
        {
            ConnectionState = state,
            Status = status
        });
    }

    private void Publish(KastnSessionSnapshot snapshot)
    {
        lock (stateGate)
        {
            Current = snapshot;
        }

        ZetlEventPublisher.Publish(SnapshotChanged, this, snapshot);
    }
}
