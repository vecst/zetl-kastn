using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using ZETL.Contracts;

namespace ZETL;

public sealed class ZetlIpcClient : IAsyncDisposable
{
    private readonly string pipeName;
    private readonly string clientName;
    private readonly bool subscribeToProjectChanges;
    private readonly NamedPipeClientStream pipe;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ZetlResponseEnvelope>> pending = new(StringComparer.Ordinal);
    private Task? readLoop;

    public ZetlIpcClient(
        string clientName,
        string? pipeName = null,
        bool subscribeToProjectChanges = true)
    {
        this.clientName = clientName;
        this.pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? ZetlIpcEndpoint.GetDefaultPipeName()
            : pipeName;
        this.subscribeToProjectChanges = subscribeToProjectChanges;
        pipe = new NamedPipeClientStream(
            ".",
            this.pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public event EventHandler<ZetlProjectChangedEvent>? ProjectChanged;
    public event EventHandler? Disconnected;

    public string? ServerInstanceId { get; private set; }
    public bool IsConnected => pipe.IsConnected && !cancellation.IsCancellationRequested;

    public async Task ConnectAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);

        await ZetlIpcFraming.WriteAsync(
            pipe,
            ZetlIpcMessage.Create(
                ZetlIpcMessageKind.Hello,
                new ZetlIpcHello
                {
                    ClientName = clientName,
                    ClientInstanceId = Guid.NewGuid().ToString("N"),
                    SubscribeToProjectChanges = subscribeToProjectChanges
                }),
            linked.Token).ConfigureAwait(false);
        var welcomeMessage = await ZetlIpcFraming.ReadAsync(pipe, linked.Token)
            .ConfigureAwait(false);
        if (welcomeMessage?.Kind != ZetlIpcMessageKind.Welcome)
        {
            throw new InvalidDataException("Zetl IPC server did not accept the handshake.");
        }

        var welcome = Deserialize<ZetlIpcWelcome>(welcomeMessage);
        if (welcome.ProtocolVersion != ZetlProtocol.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Zetl IPC protocol {welcome.ProtocolVersion} is not supported.");
        }

        ServerInstanceId = welcome.ServerInstanceId;
        readLoop = Task.Run(() => ReadLoopAsync(cancellation.Token));
    }

    public async Task<ZetlResponseEnvelope> ExecuteAsync(
        ZetlCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("The Zetl IPC client is not connected.");
        }

        var completion = new TaskCompletionSource<ZetlResponseEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(command.CommandId, completion))
        {
            throw new InvalidOperationException(
                $"Command '{command.CommandId}' is already pending.");
        }

        try
        {
            await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ZetlIpcFraming.WriteAsync(
                    pipe,
                    ZetlIpcMessage.Create(
                        ZetlIpcMessageKind.Command,
                        command,
                        command.CommandId),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                writeGate.Release();
            }

            return await completion.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            pending.TryRemove(command.CommandId, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        pipe.Dispose();
        if (readLoop is not null)
        {
            try
            {
                await readLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }

        writeGate.Dispose();
        cancellation.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? disconnectReason = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await ZetlIpcFraming.ReadAsync(pipe, cancellationToken)
                    .ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                switch (message.Kind)
                {
                    case ZetlIpcMessageKind.Response:
                    {
                        var response = Deserialize<ZetlResponseEnvelope>(message);
                        var correlationId = message.CorrelationId ?? response.CommandId;
                        if (pending.TryGetValue(correlationId, out var completion))
                        {
                            completion.TrySetResult(response);
                        }
                        break;
                    }
                    case ZetlIpcMessageKind.ProjectChanged:
                        RaiseProjectChanged(Deserialize<ZetlProjectChangedEvent>(message));
                        break;
                    case ZetlIpcMessageKind.Error:
                    {
                        var error = Deserialize<ZetlProtocolError>(message);
                        if (message.CorrelationId is not null
                            && pending.TryGetValue(message.CorrelationId, out var completion))
                        {
                            completion.TrySetException(
                                new InvalidDataException($"{error.Code}: {error.Message}"));
                        }
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException)
        {
            disconnectReason = ex;
        }
        finally
        {
            cancellation.Cancel();
            var error = disconnectReason
                ?? new IOException("The Zetl IPC connection closed.");
            foreach (var completion in pending.Values)
            {
                completion.TrySetException(error);
            }

            RaiseDisconnected();
        }
    }

    private void RaiseProjectChanged(ZetlProjectChangedEvent change)
    {
        ZetlEventPublisher.Publish(ProjectChanged, this, change);
    }

    private void RaiseDisconnected()
    {
        ZetlEventPublisher.Publish(Disconnected, this, EventArgs.Empty);
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
}
