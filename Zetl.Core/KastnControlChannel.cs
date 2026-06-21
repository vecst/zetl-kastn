using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZETL;

/// <summary>
/// A request sent to a running Kastn over its control pipe. <see cref="Command"/>
/// distinguishes an activation/restore (the default, focuses Kastn and optionally
/// navigates to a project) from a shutdown request, which expects a decision
/// reply.
/// </summary>
internal sealed record KastnControlRequest(string? ProjectId = null, string? Command = null);

/// <summary>The outcome of asking a running Kastn to shut down.</summary>
internal enum KastnShutdownDecision
{
    /// No Kastn answered the control pipe, so nothing needs to close.
    NoKastn,

    /// Kastn agreed to close (it was minimized to the tray, or the user confirmed).
    Close,

    /// The user cancelled at Kastn's confirmation dialog; nothing should close.
    Cancel,
}

/// <summary>
/// The local control pipe used to drive a running Kastn from another process —
/// the existing single-instance "activate" forward, plus Zetl's "show from tray"
/// and coordinated-shutdown signals. The pipe name is derived deterministically
/// from the user and login session, so Zetl and Kastn compute the same name
/// without sharing it at launch. Kept beside <see cref="ZetlIpcEndpoint"/> so the
/// identity hashing stays in one place.
/// </summary>
internal static class KastnControlChannel
{
    public const string ActivateCommand = "activate";
    public const string ShutdownCommand = "shutdown";
    private const string CloseReply = "close";
    private const string CancelReply = "cancel";
    private const int MaxReplyCharacters = 64;

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
        return $"kastn-control-{hash}";
    }

    /// <summary>The reply Kastn writes for a confirmed shutdown.</summary>
    public static string ReplyFor(bool close) => close ? CloseReply : CancelReply;

    /// <summary>
    /// Asks a running Kastn to focus itself (and optionally navigate to a project).
    /// Fire-and-forget: this writes the request and returns without a reply.
    /// </summary>
    public static async Task ActivateAsync(
        string? projectId,
        string? pipeName = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var linked = LinkTimeout(timeout ?? TimeSpan.FromSeconds(2), cancellationToken);
        await using var pipe = new NamedPipeClientStream(
            ".",
            ResolvePipeName(pipeName),
            PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
        await WriteRequestAsync(
            pipe,
            new KastnControlRequest(projectId, ActivateCommand),
            linked.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks a running Kastn to shut down and waits for its decision. Returns
    /// <see cref="KastnShutdownDecision.NoKastn"/> when no Kastn answers, so the
    /// caller can proceed to exit on its own.
    /// </summary>
    public static async Task<KastnShutdownDecision> RequestShutdownAsync(
        string? pipeName = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? replyTimeout = null,
        CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            ResolvePipeName(pipeName),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using (var connect = LinkTimeout(
                connectTimeout ?? TimeSpan.FromMilliseconds(750),
                cancellationToken))
            {
                await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (
            ex is TimeoutException or IOException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // No Kastn is listening (or it vanished mid-connect); nothing to close.
            return KastnShutdownDecision.NoKastn;
        }

        // The user may sit on Kastn's confirmation dialog, so the reply wait is
        // generous and separate from the short connect timeout.
        using var reply = LinkTimeout(
            replyTimeout ?? TimeSpan.FromMinutes(5),
            cancellationToken);
        await WriteRequestAsync(
            pipe,
            new KastnControlRequest(Command: ShutdownCommand),
            reply.Token).ConfigureAwait(false);

        using var reader = new StreamReader(
            pipe,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 256,
            leaveOpen: true);
        var line = await reader.ReadLineAsync(reply.Token).ConfigureAwait(false);
        return line?.Trim() is { Length: > 0 and <= MaxReplyCharacters } answer
            && string.Equals(answer, CloseReply, StringComparison.OrdinalIgnoreCase)
                ? KastnShutdownDecision.Close
                : KastnShutdownDecision.Cancel;
    }

    private static string ResolvePipeName(string? pipeName) =>
        string.IsNullOrWhiteSpace(pipeName) ? GetDefaultPipeName() : pipeName;

    private static async Task WriteRequestAsync(
        PipeStream pipe,
        KastnControlRequest request,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 256,
            leaveOpen: true)
        {
            AutoFlush = true
        };
        await writer.WriteLineAsync(
            JsonSerializer.Serialize(request).AsMemory(),
            cancellationToken).ConfigureAwait(false);
    }

    private static CancellationTokenSource LinkTimeout(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        return linked;
    }
}
