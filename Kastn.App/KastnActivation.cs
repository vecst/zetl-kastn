using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ZETL;

namespace KASTN;

/// <summary>
/// Hosts Kastn's control pipe: the single-instance "activate" forward plus the
/// "show from tray" and coordinated-shutdown signals Zetl sends. Activation
/// requests are fire-and-forget; a shutdown request waits for a decision the
/// host supplies through <see cref="ShutdownRequested"/> and replies with it.
/// </summary>
internal sealed class KastnControlServer : IAsyncDisposable
{
    private const int MaxRequestCharacters = 4096;
    private readonly string pipeName;
    private readonly CancellationTokenSource cancellation = new();
    private Task? acceptLoop;

    public KastnControlServer(string? pipeName = null)
    {
        this.pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? KastnControlChannel.GetDefaultPipeName()
            : pipeName;
    }

    /// Raised for an activation/restore request (focus Kastn, optional project).
    public event EventHandler<KastnControlRequest>? ActivationRequested;

    /// <summary>
    /// Invoked for a shutdown request. Returns true to close Kastn, false to keep
    /// it running (the user cancelled). The reply is sent before
    /// <see cref="ShutdownConfirmed"/> runs, so Kastn can exit afterward without
    /// dropping the requester's pipe.
    /// </summary>
    public Func<Task<bool>>? ShutdownRequested { get; set; }

    /// Invoked after a "close" reply has been flushed, so Kastn can actually exit.
    public Action? ShutdownConfirmed { get; set; }

    /// Invoked when an approved reply cannot reach the requester. Release the
    /// pending approval so Kastn can resume activation or retry closing.
    public Action? ShutdownAbandoned { get; set; }

    public KastnControlRequest? PendingRequest { get; private set; }

    public void Start()
    {
        acceptLoop ??= Task.Run(() => AcceptLoopAsync(cancellation.Token));
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cancellation.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(
                    pipe,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: true);
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null || line.Length > MaxRequestCharacters)
                {
                    continue;
                }

                var request = JsonSerializer.Deserialize<KastnControlRequest>(line);
                if (request is null)
                {
                    continue;
                }

                if (string.Equals(
                        request.Command,
                        KastnControlChannel.ShutdownCommand,
                        StringComparison.OrdinalIgnoreCase))
                {
                    await HandleShutdownAsync(pipe, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    PendingRequest = request;
                    ActivationRequested?.Invoke(this, request);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (
                ex is IOException or InvalidDataException or JsonException)
            {
            }
        }
    }

    private async Task HandleShutdownAsync(
        PipeStream pipe,
        CancellationToken cancellationToken)
    {
        // No handler means Kastn can't make the decision; treat as a cancel so the
        // requester stays running rather than killing an app that didn't consent.
        var close = ShutdownRequested is { } handler
            && await handler().WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 256,
                leaveOpen: true)
            {
                AutoFlush = true
            })
            {
                await writer.WriteLineAsync(
                    KastnControlChannel.ReplyFor(close).AsMemory(),
                    cancellationToken).ConfigureAwait(false);
            }

            // Flush the reply before tearing down the process and its pipe.
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (close) ShutdownAbandoned?.Invoke();
            throw;
        }
        if (close)
        {
            ShutdownConfirmed?.Invoke();
        }
    }
}
