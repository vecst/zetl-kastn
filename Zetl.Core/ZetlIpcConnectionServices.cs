using System.Text.Json;
using System.Threading.Channels;
using ZETL.Contracts;

namespace ZETL;

internal sealed record ZetlIpcServerOptions
{
    public static ZetlIpcServerOptions Default { get; } = new();

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan FrameProgressTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public int ResponseCapacity { get; init; } = 256;
}

internal sealed class ZetlIpcServerHandshake(
    string serverInstanceId,
    ZetlIpcServerOptions options)
{
    public async Task<ZetlIpcHello?> AcceptAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        handshake.CancelAfter(options.HandshakeTimeout);

        ZetlIpcMessage? helloMessage;
        try
        {
            helloMessage = await ZetlIpcFraming.ReadAsync(
                stream,
                handshake.Token,
                options.FrameProgressTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"IPC client did not complete its hello within {options.HandshakeTimeout.TotalMilliseconds:0} ms.");
        }

        if (helloMessage?.Kind != ZetlIpcMessageKind.Hello
            || helloMessage.ProtocolVersion != ZetlProtocol.CurrentVersion)
        {
            await ZetlIpcFraming.WriteAsync(
                stream,
                ZetlIpcProtocolMessages.Error(
                    "handshake_required",
                    $"Protocol {ZetlProtocol.CurrentVersion} hello required."),
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        var hello = ZetlIpcProtocolMessages.Deserialize<ZetlIpcHello>(helloMessage);
        await ZetlIpcFraming.WriteAsync(
            stream,
            ZetlIpcMessage.Create(
                ZetlIpcMessageKind.Welcome,
                new ZetlIpcWelcome
                {
                    ServerInstanceId = serverInstanceId,
                    ProtocolVersion = ZetlProtocol.CurrentVersion
                }),
            cancellationToken).ConfigureAwait(false);
        return hello;
    }
}

internal sealed class ZetlIpcInboundCommandHandler(
    ZetlProjectService service,
    Action<string>? log,
    Func<ZetlCommandEnvelope, bool>? dropResponseForTesting)
{
    public ZetlIpcInboundResult Handle(ZetlIpcMessage message)
    {
        if (message.Kind != ZetlIpcMessageKind.Command)
        {
            return ZetlIpcInboundResult.ReplyWith(
                ZetlIpcProtocolMessages.Error(
                    "command_required",
                    "Only command messages are accepted after handshake.",
                    message.CorrelationId));
        }

        ZetlCommandEnvelope command;
        try
        {
            command = ZetlIpcProtocolMessages.Deserialize<ZetlCommandEnvelope>(message);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            log?.Invoke(
                $"IPC client sent an invalid command ({ex.GetType().Name}).");
            return ZetlIpcInboundResult.ReplyWith(
                ZetlIpcProtocolMessages.Error(
                    "command_invalid",
                    ex.Message,
                    message.CorrelationId));
        }

        var response = service.Execute(command);
        if (response.Status != ZetlResponseStatus.Success)
        {
            log?.Invoke(
                $"IPC command {command.Kind} ({command.CommandId}) returned {response.Status}.");
        }

        if (dropResponseForTesting?.Invoke(command) == true)
        {
            return ZetlIpcInboundResult.Close;
        }

        return ZetlIpcInboundResult.ReplyWith(
            ZetlIpcMessage.Create(
                ZetlIpcMessageKind.Response,
                response,
                message.CorrelationId ?? command.CommandId));
    }
}

internal sealed record ZetlIpcInboundResult(
    ZetlIpcMessage? Reply,
    bool CloseConnection)
{
    public static ZetlIpcInboundResult Close { get; } = new(null, true);

    public static ZetlIpcInboundResult ReplyWith(ZetlIpcMessage message) =>
        new(message, false);
}

internal sealed class ZetlIpcOutboundDispatcher
{
    private readonly Channel<ZetlIpcMessage> replies;
    private readonly Channel<bool> signal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    private readonly object changesGate = new();
    private readonly Dictionary<string, ZetlProjectChangedEvent> pendingChanges =
        new(StringComparer.Ordinal);
    private bool completed;

    public ZetlIpcOutboundDispatcher(int responseCapacity)
    {
        if (responseCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(responseCapacity));
        }

        replies = Channel.CreateBounded<ZetlIpcMessage>(
            new BoundedChannelOptions(responseCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
    }

    public bool TryQueueReply(ZetlIpcMessage message)
    {
        if (!replies.Writer.TryWrite(message))
        {
            return false;
        }

        Signal();
        return true;
    }

    public bool QueueProjectChange(ZetlProjectChangedEvent change)
    {
        lock (changesGate)
        {
            if (completed)
            {
                return false;
            }

            if (pendingChanges.TryGetValue(change.ProjectId, out var pending)
                && pending.ProjectChangeSequence >= change.ProjectChangeSequence)
            {
                return true;
            }

            pendingChanges[change.ProjectId] = change;
        }

        Signal();
        return true;
    }

    public void Complete()
    {
        lock (changesGate)
        {
            completed = true;
        }

        replies.Writer.TryComplete();
        signal.Writer.TryComplete();
    }

    public async Task WriteToAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var wroteMessage = false;
            while (replies.Reader.TryRead(out var reply))
            {
                wroteMessage = true;
                await ZetlIpcFraming.WriteAsync(stream, reply, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (TryTakeProjectChange(out var change))
            {
                wroteMessage = true;
                await ZetlIpcFraming.WriteAsync(
                    stream,
                    ZetlIpcMessage.Create(
                        ZetlIpcMessageKind.ProjectChanged,
                        change),
                    cancellationToken).ConfigureAwait(false);
            }

            if (wroteMessage)
            {
                continue;
            }

            if (IsDrainedAndCompleted())
            {
                return;
            }

            if (!await signal.Reader.WaitToReadAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                return;
            }

            while (signal.Reader.TryRead(out _))
            {
            }
        }
    }

    private bool TryTakeProjectChange(out ZetlProjectChangedEvent change)
    {
        lock (changesGate)
        {
            if (pendingChanges.Count == 0)
            {
                change = null!;
                return false;
            }

            var pair = pendingChanges.First();
            pendingChanges.Remove(pair.Key);
            change = pair.Value;
            return true;
        }
    }

    private bool IsDrainedAndCompleted()
    {
        lock (changesGate)
        {
            return completed
                && pendingChanges.Count == 0
                && replies.Reader.Completion.IsCompleted;
        }
    }

    private void Signal() => signal.Writer.TryWrite(true);
}

internal static class ZetlIpcProtocolMessages
{
    public static T Deserialize<T>(ZetlIpcMessage message)
    {
        if (message.Payload is null)
        {
            throw new InvalidDataException($"{message.Kind} message has no payload.");
        }

        return message.Payload.Value.Deserialize<T>(ZetlProtocolJson.Options)
            ?? throw new InvalidDataException($"{message.Kind} payload is invalid.");
    }

    public static ZetlIpcMessage Error(
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
