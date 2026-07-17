using System.Buffers.Binary;
using System.Text.Json;
using ZETL.Contracts;

namespace ZETL;

internal static class ZetlIpcFraming
{
    // Image assets are capped at 25 MiB before storage. JSON represents byte[]
    // as base64, so reserve enough room for that expansion plus the envelope.
    public const int MaxMessageBytes = 36 * 1024 * 1024;

    public static async Task WriteAsync(
        Stream stream,
        ZetlIpcMessage message,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            message,
            ZetlProtocolJson.Options);
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidDataException(
                $"IPC message is {payload.Length} bytes; the limit is {MaxMessageBytes}.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ZetlIpcMessage?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken,
        TimeSpan? progressTimeout = null)
    {
        var header = new byte[sizeof(int)];
        if (!await ReadExactlyOrEofAsync(
                stream,
                header,
                progressTimeout,
                waitIndefinitelyForFirstByte: true,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException(
                $"IPC frame length {length} is outside the allowed range.");
        }

        var payload = new byte[length];
        _ = await ReadExactlyOrEofAsync(
            stream,
            payload,
            progressTimeout,
            waitIndefinitelyForFirstByte: false,
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ZetlIpcMessage>(
            payload,
            ZetlProtocolJson.Options)
            ?? throw new InvalidDataException("IPC frame contained no message.");
    }

    private static async Task<bool> ReadExactlyOrEofAsync(
        Stream stream,
        byte[] buffer,
        TimeSpan? progressTimeout,
        bool waitIndefinitelyForFirstByte,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var applyProgressTimeout = progressTimeout is { } timeout
                && timeout != Timeout.InfiniteTimeSpan
                && (!waitIndefinitelyForFirstByte || read > 0);
            var count = applyProgressTimeout
                ? await ReadWithProgressTimeoutAsync(
                    stream,
                    buffer.AsMemory(read),
                    progressTimeout!.Value,
                    cancellationToken).ConfigureAwait(false)
                : await stream.ReadAsync(
                    buffer.AsMemory(read),
                    cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (read == 0 && waitIndefinitelyForFirstByte)
                {
                    return false;
                }

                throw new EndOfStreamException("IPC frame ended before its declared length.");
            }

            read += count;
        }

        return true;
    }

    private static async ValueTask<int> ReadWithProgressTimeoutAsync(
        Stream stream,
        Memory<byte> buffer,
        TimeSpan progressTimeout,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(progressTimeout);
        try
        {
            return await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"IPC frame made no progress for {progressTimeout.TotalMilliseconds:0} ms.");
        }
    }
}
