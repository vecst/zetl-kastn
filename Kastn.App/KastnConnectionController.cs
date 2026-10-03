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
    private readonly TimeSpan uncertainRetryTimeout;
    private readonly CancellationTokenSource cancellation = new();
    private readonly KastnRefreshCoordinator refreshCoordinator;
    private readonly object stateGate = new();
    private ZetlIpcClient? client;
    private TaskCompletionSource connectionChanged = NewConnectionSignal();
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
        TimeSpan? retryDelay = null,
        TimeSpan? uncertainRetryTimeout = null)
    {
        this.launchZetl = launchZetl;
        this.pipeName = pipeName;
        this.connectTimeout = connectTimeout ?? TimeSpan.FromMilliseconds(750);
        this.retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
        this.uncertainRetryTimeout = uncertainRetryTimeout ?? TimeSpan.FromSeconds(5);
        Current = new KastnSessionSnapshot(
            KastnConnectionState.Connecting,
            "Connecting to Zetl...",
            [],
            null);
        refreshCoordinator = new KastnRefreshCoordinator(
            RefreshCoreAsync,
            PublishRefreshFailure,
            cancellation.Token);
    }

    public event EventHandler<KastnSessionSnapshot>? SnapshotChanged;

    public KastnSessionSnapshot Current { get; private set; }

    public bool HasLiveConnection
    {
        get
        {
            lock (stateGate)
            {
                return client is { IsConnected: true };
            }
        }
    }

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
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        refreshCoordinator.RefreshAsync(cancellationToken);

    // Apply pending command/event changes, then republish the current projection
    // when a UI handler needs to settle selection. A clean projection needs no IPC.
    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        if (!await refreshCoordinator.FlushAsync(cancellationToken).ConfigureAwait(false))
        {
            // Republishing for selection must not write an older captured value
            // back over a concurrent refresh or connection-state transition.
            ZetlEventPublisher.Publish(SnapshotChanged, this, Current);
        }
    }

    public IAsyncDisposable DeferRefresh() => refreshCoordinator.Defer();

    public async Task<ZetlResponseEnvelope> ExecuteAsync(
        ZetlCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        var connected = client;
        if (connected is null || !connected.IsConnected)
        {
            throw new InvalidOperationException("Zetl is offline.");
        }

        var response = await ExecuteWithUncertainRetryAsync(
            connected,
            command,
            cancellationToken).ConfigureAwait(false);
        if (response.Status == ZetlResponseStatus.Success && !ZetlContractRules.IsReadOnly(command.Kind))
        {
            await refreshCoordinator.AfterMutationAsync(cancellationToken).ConfigureAwait(false);
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

        return await ExecuteWithUncertainRetryAsync(
            connected,
            command,
            cancellationToken).ConfigureAwait(false);
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

        await refreshCoordinator.DisposeAsync().ConfigureAwait(false);
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

            SetClient(nextClient);
            hasEverConnected = true;
            launchAttemptedForOutage = false;
            var disconnected = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            nextClient.Disconnected += (_, _) => disconnected.TrySetResult();
            nextClient.ProjectChanged += OnProjectChanged;

            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
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
                ClearClient(nextClient);

                await nextClient.DisposeAsync().ConfigureAwait(false);
            }

            Publish(
                KastnConnectionState.Offline,
                "Zetl disconnected. Kastn will reconnect automatically.");
            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnProjectChanged(object? _sender, ZetlProjectChangedEvent change)
    {
        // A delayed slip/bucket notification may describe content already fetched
        // by the command follow-up. Project events also carry workspace/lane changes
        // without advancing this sequence, so those must always invalidate.
        var snapshot = Current;
        if (change.EntityKind != ZetlEntityKind.Project
            && snapshot.ConnectionState == KastnConnectionState.Online
            && snapshot.Projects.Any(project => project.Id == change.ProjectId
                && project.ChangeSequence >= change.ProjectChangeSequence))
        {
            return;
        }
        refreshCoordinator.Request();
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var connected = client;
        if (connected is null)
        {
            return;
        }
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
            // A list refresh can change lane/status summaries without changing
            // this project's content. Reuse its snapshot when revisions match;
            // still publish so the UI can settle selection after a batch.
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
            projectSnapshot)
        {
            ServerInstanceId = connected.ServerInstanceId
        });
    }

    private static void EnsureSuccess(ZetlResponseEnvelope response)
    {
        if (response.Status != ZetlResponseStatus.Success)
        {
            throw new InvalidOperationException(
                response.Error?.Message ?? $"Zetl returned {response.Status}.");
        }
    }

    private async Task<ZetlResponseEnvelope> ExecuteWithUncertainRetryAsync(
        ZetlIpcClient initialClient,
        ZetlCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await initialClient.ExecuteAsync(command, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ZetlCommandOutcomeUnknownException firstUnknown) when (
            !cancellationToken.IsCancellationRequested
            && !cancellation.IsCancellationRequested)
        {
            var originalServerInstanceId = initialClient.ServerInstanceId;
            var lastUnknown = firstUnknown;
            using var timeout = new CancellationTokenSource(uncertainRetryTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                cancellation.Token,
                timeout.Token);

            try
            {
                while (true)
                {
                    ZetlIpcClient? retryClient;
                    Task connectionSignal;
                    lock (stateGate)
                    {
                        retryClient = client;
                        connectionSignal = connectionChanged.Task;
                    }

                    if (retryClient is { IsConnected: true })
                    {
                        var sameServer = string.Equals(
                            originalServerInstanceId,
                            retryClient.ServerInstanceId,
                            StringComparison.Ordinal);
                        if (!sameServer && !ZetlContractRules.IsReadOnly(command.Kind))
                        {
                            throw new ZetlCommandOutcomeUnknownException(
                                command.CommandId,
                                $"The result of {command.Kind} is unknown and Zetl restarted before it could be reconciled.",
                                lastUnknown,
                                serverInstanceChanged: true);
                        }

                        try
                        {
                            // The same command id is essential: the still-running
                            // Zetl instance returns its cached response instead of
                            // executing a mutation twice.
                            return await retryClient.ExecuteAsync(command, linked.Token)
                                .ConfigureAwait(false);
                        }
                        catch (ZetlCommandOutcomeUnknownException ex)
                        {
                            lastUnknown = ex;
                            continue;
                        }
                        catch (InvalidOperationException) when (!retryClient.IsConnected)
                        {
                            continue;
                        }
                    }

                    await connectionSignal.WaitAsync(linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException ex) when (
                timeout.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
                && !cancellation.IsCancellationRequested)
            {
                throw new ZetlCommandOutcomeUnknownException(
                    command.CommandId,
                    $"The result of {command.Kind} is still unknown after waiting for Zetl to reconnect.",
                    ex);
            }
        }
    }

    private void PublishRefreshFailure(Exception exception)
    {
        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        Publish(Current with
        {
            Status = $"Kastn could not refresh from Zetl. {exception.Message}"
        });
    }

    private void SetClient(ZetlIpcClient value)
    {
        TaskCompletionSource signal;
        lock (stateGate)
        {
            client = value;
            signal = connectionChanged;
            connectionChanged = NewConnectionSignal();
        }
        signal.TrySetResult();
    }

    private void ClearClient(ZetlIpcClient expected)
    {
        TaskCompletionSource? signal = null;
        lock (stateGate)
        {
            if (ReferenceEquals(client, expected))
            {
                client = null;
                signal = connectionChanged;
                connectionChanged = NewConnectionSignal();
            }
        }
        signal?.TrySetResult();
    }

    private static TaskCompletionSource NewConnectionSignal() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);

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
