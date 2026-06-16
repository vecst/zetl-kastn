using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KASTN;

internal sealed record KastnActivationRequest(string? ProjectId);

internal static class KastnActivationEndpoint
{
    public static string GetDefaultPipeName()
    {
        var session = OperatingSystem.IsWindows()
            ? Process.GetCurrentProcess().SessionId.ToString()
            : Environment.GetEnvironmentVariable("XDG_SESSION_ID")
                ?? Environment.GetEnvironmentVariable("DISPLAY")
                ?? "default";
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}|{session}";
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16]
            .ToLowerInvariant();
        return $"kastn-activation-{hash}";
    }
}

internal sealed class KastnActivationServer : IAsyncDisposable
{
    private const int MaxRequestCharacters = 4096;
    private readonly string pipeName;
    private readonly CancellationTokenSource cancellation = new();
    private Task? acceptLoop;

    public KastnActivationServer(string? pipeName = null)
    {
        this.pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? KastnActivationEndpoint.GetDefaultPipeName()
            : pipeName;
    }

    public event EventHandler<KastnActivationRequest>? ActivationRequested;

    public KastnActivationRequest? PendingRequest { get; private set; }

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
                PipeDirection.In,
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

                var request = JsonSerializer.Deserialize<KastnActivationRequest>(line);
                if (request is null)
                {
                    continue;
                }

                PendingRequest = request;
                ActivationRequested?.Invoke(this, request);
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
}

internal static class KastnActivationClient
{
    public static async Task SendAsync(
        string? projectId,
        string? pipeName = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedPipeName = string.IsNullOrWhiteSpace(pipeName)
            ? KastnActivationEndpoint.GetDefaultPipeName()
            : pipeName;
        using var timeoutCancellation = new CancellationTokenSource(
            timeout ?? TimeSpan.FromSeconds(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        await using var pipe = new NamedPipeClientStream(
            ".",
            resolvedPipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
        await using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true
        };
        await writer.WriteLineAsync(
            JsonSerializer.Serialize(new KastnActivationRequest(projectId))
                .AsMemory(),
            linked.Token).ConfigureAwait(false);
    }
}
