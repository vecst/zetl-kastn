using System.IO.Pipes;
using System.Text;

namespace ZETL;

/// <summary>
/// A local pipe a second launch of Zetl uses to reach the one already running,
/// so opening Zetl from the app menu (or a desktop without a tray, like stock
/// GNOME) shows Zetl's menu instead of a dead end. Named per user and session
/// like Zetl's other pipes. Requests are single lines; unknown ones are ignored.
/// </summary>
internal static class ZetlControlChannel
{
    public const string ShowMenuCommand = "menu";
    private const int MaxRequestCharacters = 256;

    public static string GetDefaultPipeName() => $"zetl-control-{ZetlIpcEndpoint.SessionHash()}";

    /// <summary>Asks the running Zetl to show its menu. False when none answered.</summary>
    public static async Task<bool> RequestAsync(
        string command,
        string? pipeName = null,
        TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName ?? GetDefaultPipeName(),
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(cancellation.Token).ConfigureAwait(false);
            var line = Encoding.UTF8.GetBytes(command + "\n");
            await pipe.WriteAsync(line, cancellation.Token).ConfigureAwait(false);
            await pipe.FlushAsync(cancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Serves <see cref="ZetlControlChannel"/> inside the running Zetl. A listener
/// stays open between connections: on Unix the pipe is a socket shared by every
/// server instance of the name, so a gap would reset a client that connected in it.
/// </summary>
internal sealed class ZetlControlServer : IAsyncDisposable
{
    private readonly string pipeName;
    private readonly Action<string> onRequest;
    private readonly Action<string>? log;
    private readonly CancellationTokenSource cancellation = new();
    private Task? acceptLoop;

    public ZetlControlServer(Action<string> onRequest, Action<string>? log = null, string? pipeName = null)
    {
        this.onRequest = onRequest;
        this.log = log;
        this.pipeName = pipeName ?? ZetlControlChannel.GetDefaultPipeName();
    }

    public void Start() => acceptLoop ??= Task.Run(() => AcceptLoopAsync(cancellation.Token));

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
        var listener = CreateServerStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await listener.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException)
                {
                    var spoiled = listener;
                    listener = CreateServerStream();
                    await spoiled.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                var pipe = listener;
                listener = CreateServerStream();
                await using (pipe.ConfigureAwait(false))
                {
                    try
                    {
                        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 256, leaveOpen: true);
                        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                        if (line is { Length: > 0 and <= 256 })
                        {
                            onRequest(line.Trim());
                        }
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException)
                    {
                        // A requester that went away mid-request asked for nothing.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Zetl control pipe stopped: {ex.Message}");
        }
        finally
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    private NamedPipeServerStream CreateServerStream() => new(
        pipeName,
        PipeDirection.In,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
