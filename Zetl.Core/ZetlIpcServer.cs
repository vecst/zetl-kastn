using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using ZETL.Contracts;

namespace ZETL;

internal sealed class ZetlIpcServer : IDisposable
{
    private readonly string pipeName;
    private readonly ZetlProjectService service;
    private readonly Action<string>? log;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentDictionary<int, ClientConnection> clients = new();
    private readonly string serverInstanceId = Guid.NewGuid().ToString("N");
    private Task? acceptLoop;
    private int nextClientId;

    public ZetlIpcServer(
        ZetlProjectService service,
        string? pipeName = null,
        Action<string>? log = null)
    {
        this.service = service;
        this.pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? ZetlIpcEndpoint.GetDefaultPipeName()
            : pipeName;
        this.log = log;
    }

    public string PipeName => pipeName;

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
        private const int OutboundCapacity = 256;

        private readonly int clientId;
        private readonly NamedPipeServerStream pipe;
        private readonly string serverInstanceId;
        private readonly ZetlProjectService service;
        private readonly Action<string>? log;
        private readonly Action onClosed;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Channel<ZetlIpcMessage> outbound = Channel.CreateBounded<ZetlIpcMessage>(
            new BoundedChannelOptions(OutboundCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        private EventHandler<ZetlProjectChangedEvent>? changeHandler;
        private Task? runTask;

        public ClientConnection(
            int clientId,
            NamedPipeServerStream pipe,
            string serverInstanceId,
            ZetlProjectService service,
            Action<string>? log,
            Action onClosed)
        {
            this.clientId = clientId;
            this.pipe = pipe;
            this.serverInstanceId = serverInstanceId;
            this.service = service;
            this.log = log;
            this.onClosed = onClosed;
        }

        public void Start(CancellationToken serverCancellation)
        {
            runTask = Task.Run(() => RunAsync(serverCancellation));
        }

        public void Dispose()
        {
            cancellation.Cancel();
            outbound.Writer.TryComplete();
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
                var helloMessage = await ZetlIpcFraming.ReadAsync(pipe, token)
                    .ConfigureAwait(false);
                if (helloMessage?.Kind != ZetlIpcMessageKind.Hello
                    || helloMessage.ProtocolVersion != ZetlProtocol.CurrentVersion)
                {
                    log?.Invoke(
                        $"IPC client {clientId} rejected during handshake "
                        + $"(protocol {helloMessage?.ProtocolVersion.ToString() ?? "missing"}).");
                    await ZetlIpcFraming.WriteAsync(
                        pipe,
                        Error(
                            "handshake_required",
                            $"Protocol {ZetlProtocol.CurrentVersion} hello required."),
                        token).ConfigureAwait(false);
                    return;
                }

                var hello = Deserialize<ZetlIpcHello>(helloMessage);
                log?.Invoke(
                    $"IPC client {clientId} connected as '{hello.ClientName}' "
                    + $"(subscribe={hello.SubscribeToProjectChanges}).");
                await ZetlIpcFraming.WriteAsync(
                    pipe,
                    ZetlIpcMessage.Create(
                        ZetlIpcMessageKind.Welcome,
                        new ZetlIpcWelcome
                        {
                            ServerInstanceId = serverInstanceId,
                            ProtocolVersion = ZetlProtocol.CurrentVersion
                        }),
                    token).ConfigureAwait(false);

                if (hello.SubscribeToProjectChanges)
                {
                    changeHandler = (_, change) =>
                    {
                        if (!TryQueue(ZetlIpcMessage.Create(
                                ZetlIpcMessageKind.ProjectChanged,
                                change)))
                        {
                            cancellation.Cancel();
                        }
                    };
                    service.ProjectChanged += changeHandler;
                }

                var writer = WriteLoopAsync(token);
                await ReadLoopAsync(token).ConfigureAwait(false);
                cancellation.Cancel();
                outbound.Writer.TryComplete();
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
                    message = await ZetlIpcFraming.ReadAsync(pipe, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
                {
                    log?.Invoke(
                        $"IPC client {clientId} sent an invalid message "
                        + $"({ex.GetType().Name}).");
                    if (!TryQueue(Error("message_invalid", ex.Message)))
                    {
                        return;
                    }
                    continue;
                }

                if (message is null)
                {
                    return;
                }

                if (message.Kind != ZetlIpcMessageKind.Command)
                {
                    if (!TryQueue(Error(
                            "command_required",
                            "Only command messages are accepted after handshake.",
                            message.CorrelationId)))
                    {
                        return;
                    }
                    continue;
                }

                ZetlCommandEnvelope command;
                try
                {
                    command = Deserialize<ZetlCommandEnvelope>(message);
                }
                catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
                {
                    log?.Invoke(
                        $"IPC client {clientId} sent an invalid command "
                        + $"({ex.GetType().Name}).");
                    if (!TryQueue(Error(
                            "command_invalid",
                            ex.Message,
                            message.CorrelationId)))
                    {
                        return;
                    }
                    continue;
                }

                var response = service.Execute(command);
                if (response.Status != ZetlResponseStatus.Success)
                {
                    log?.Invoke(
                        $"IPC command {command.Kind} ({command.CommandId}) returned "
                        + $"{response.Status}.");
                }

                if (!TryQueue(ZetlIpcMessage.Create(
                        ZetlIpcMessageKind.Response,
                        response,
                        message.CorrelationId ?? command.CommandId)))
                {
                    return;
                }
            }
        }

        private async Task WriteLoopAsync(CancellationToken cancellationToken)
        {
            await foreach (var message in outbound.Reader.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                await ZetlIpcFraming.WriteAsync(pipe, message, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private bool TryQueue(ZetlIpcMessage message)
        {
            return outbound.Writer.TryWrite(message);
        }

        private static T Deserialize<T>(ZetlIpcMessage message)
        {
            if (message.Payload is null)
            {
                throw new InvalidDataException($"{message.Kind} message has no payload.");
            }

            return message.Payload.Value.Deserialize<T>(ZetlProtocolJson.Options)
                ?? throw new InvalidDataException($"{message.Kind} payload is invalid.");
        }

        private static ZetlIpcMessage Error(
            string code,
            string message,
            string? correlationId = null)
        {
            return ZetlIpcMessage.Create(
                ZetlIpcMessageKind.Error,
                new ZetlProtocolError
                {
                    Code = code,
                    Message = message
                },
                correlationId);
        }
    }
}
