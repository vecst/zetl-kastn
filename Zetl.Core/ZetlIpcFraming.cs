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
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        if (!await ReadExactlyOrEofAsync(stream, header, cancellationToken).ConfigureAwait(false))
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
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ZetlIpcMessage>(
            payload,
            ZetlProtocolJson.Options)
            ?? throw new InvalidDataException("IPC frame contained no message.");
    }

    private static async Task<bool> ReadExactlyOrEofAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(
                buffer.AsMemory(read),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("IPC frame ended inside its header.");
            }

            read += count;
        }

        return true;
    }
}
