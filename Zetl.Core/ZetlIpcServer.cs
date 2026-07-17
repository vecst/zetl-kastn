using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Linq;
using ZETL.Contracts;

namespace ZETL;

internal sealed class ZetlIpcServer : IDisposable
{
    private readonly string pipeName;
    private readonly ZetlProjectService service;
    private readonly Action<string>? log;
    private readonly Func<ZetlCommandEnvelope, bool>? dropResponseForTesting;
    private readonly ZetlIpcServerOptions options;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentDictionary<int, ClientConnection> clients = new();
    private readonly string serverInstanceId = Guid.NewGuid().ToString("N");
    private Task? acceptLoop;
    private int nextClientId;

    public ZetlIpcServer(
        ZetlProjectService service,
        string? pipeName = null,
        Action<string>? log = null)
        : this(service, pipeName, log, dropResponseForTesting: null, options: null)
    {
    }

    internal ZetlIpcServer(
        ZetlProjectService service,
        string? pipeName,
        Action<string>? log,
        Func<ZetlCommandEnvelope, bool>? dropResponseForTesting,
        ZetlIpcServerOptions? options = null)
    {
        this.service = service;
        this.pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? ZetlIpcEndpoint.GetDefaultPipeName()
            : pipeName;
        this.log = log;
        this.dropResponseForTesting = dropResponseForTesting;
        this.options = options ?? ZetlIpcServerOptions.Default;
    }

    public string PipeName => pipeName;

    /// <summary>
    /// True when a client identified itself with the given name in its hello.
    /// Used by the host to decide whether to focus a running Kastn or launch one.
    /// </summary>
    public bool HasClient(string name) => clients.Values.Any(
        client => string.Equals(client.ClientName, name, StringComparison.OrdinalIgnoreCase));

    public void Start()
    {
        if (acceptLoop is not null)
        {
            return;
        }

        acceptLoop = Task.Run(() => AcceptLoopAsync(cancellation.Token));
    }

    public void Dispose()
    {
        cancellation.Cancel();
        foreach (var client in clients.Values)
        {
            client.Dispose();
        }

        try
        {
            acceptLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (
            ex.InnerExceptions.All(error => error is OperationCanceledException))
        {
        }

        cancellation.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var clientId = Interlocked.Increment(ref nextClientId);
                var connection = new ClientConnection(
                    clientId,
                    pipe,
                    serverInstanceId,
                    service,
                    log,
                    dropResponseForTesting,
                    options,
                    () => clients.TryRemove(clientId, out _));
                clients[clientId] = connection;
                pipe = null;
                connection.Start(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                log?.Invoke($"IPC accept failed ({ex.GetType().Name}): {ex.Message}");
                try
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private sealed class ClientConnection : IDisposable
    {
        private readonly int clientId;
        private readonly NamedPipeServerStream pipe;
        private readonly ZetlProjectService service;
        private readonly Action<string>? log;
        private readonly ZetlIpcServerOptions options;
        private readonly Action onClosed;
        private readonly CancellationTokenSource cancellation = new();
        private readonly ZetlIpcServerHandshake handshake;
        private readonly ZetlIpcInboundCommandHandler commandHandler;
        private readonly ZetlIpcOutboundDispatcher outbound;
        private EventHandler<ZetlProjectChangedEvent>? changeHandler;
        private Task? runTask;

        public ClientConnection(
            int clientId,
            NamedPipeServerStream pipe,
            string serverInstanceId,
            ZetlProjectService service,
            Action<string>? log,
            Func<ZetlCommandEnvelope, bool>? dropResponseForTesting,
            ZetlIpcServerOptions options,
            Action onClosed)
        {
            this.clientId = clientId;
            this.pipe = pipe;
            this.service = service;
            this.log = log;
            this.options = options;
            this.onClosed = onClosed;
            handshake = new ZetlIpcServerHandshake(serverInstanceId, options);
            commandHandler = new ZetlIpcInboundCommandHandler(
                service,
                log,
                dropResponseForTesting);
            outbound = new ZetlIpcOutboundDispatcher(options.ResponseCapacity);
        }

        // The client name from its hello, available once the handshake completes.
        public string? ClientName { get; private set; }

        public void Start(CancellationToken serverCancellation)
        {
            runTask = Task.Run(() => RunAsync(serverCancellation));
        }

        public void Dispose()
        {
            cancellation.Cancel();
            outbound.Complete();
            pipe.Dispose();
            cancellation.Dispose();
        }

        private async Task RunAsync(CancellationToken serverCancellation)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                serverCancellation,
                cancellation.Token);
            var token = linked.Token;
            try
            {
                var hello = await handshake.AcceptAsync(pipe, token)
                    .ConfigureAwait(false);
                if (hello is null)
                {
                    log?.Invoke(
                        $"IPC client {clientId} rejected during handshake.");
                    return;
                }

                ClientName = hello.ClientName;
                log?.Invoke(
                    $"IPC client {clientId} connected as '{hello.ClientName}' "
                    + $"(subscribe={hello.SubscribeToProjectChanges}).");
                if (hello.SubscribeToProjectChanges)
                {
                    changeHandler = (_, change) =>
                    {
                        if (!outbound.QueueProjectChange(change))
                        {
                            cancellation.Cancel();
                        }
                    };
                    service.ProjectChanged += changeHandler;
                }

                var writer = outbound.WriteToAsync(pipe, token);
                await ReadLoopAsync(token).ConfigureAwait(false);
                cancellation.Cancel();
                outbound.Complete();
                await writer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (EndOfStreamException)
            {
            }
            catch (IOException)
            {
            }
            catch (Exception ex)
            {
                log?.Invoke(
                    $"IPC client {clientId} failed ({ex.GetType().Name}): {ex.Message}");
            }
            finally
            {
                if (changeHandler is not null)
                {
                    service.ProjectChanged -= changeHandler;
                }

                pipe.Dispose();
                onClosed();
            }
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ZetlIpcMessage? message;
                try
                {
                    message = await ZetlIpcFraming.ReadAsync(
                            pipe,
                            cancellationToken,
                            options.FrameProgressTimeout)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
                {
                    log?.Invoke(
                        $"IPC client {clientId} sent an invalid message "
                        + $"({ex.GetType().Name}).");
                    if (!outbound.TryQueueReply(
                            ZetlIpcProtocolMessages.Error("message_invalid", ex.Message)))
                    {
                        return;
                    }
                    continue;
                }

                if (message is null)
                {
                    return;
                }

                var result = commandHandler.Handle(message);
                if (result.CloseConnection)
                {
                    // Fault injection: the service has completed (and cached) the
                    // result, but this connection loses the response. A reconnect
                    // using the same command id must recover the cached response.
                    pipe.Dispose();
                    return;
                }

                if (result.Reply is not null
                    && !outbound.TryQueueReply(result.Reply))
                {
                    return;
                }
            }
        }
    }
}
