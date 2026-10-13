using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ZETL;

/// <summary>
/// Asks Hyprland where the mouse is, over the local socket hyprctl uses. X11
/// apps can't see that while the mouse is over a native Wayland window (X11's
/// pointer stays wherever it last left an X11 window), so popups would open on
/// the wrong monitor. Only used when Hyprland is running; anything unexpected
/// answers null.
/// </summary>
internal static class HyprlandIpc
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    public static (int X, int Y)? TryGetCursor()
    {
        if (Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE") is not { Length: > 0 } signature
            || Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is not { Length: > 0 } runtime
            || signature.Contains('/'))
        {
            return null;
        }

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            {
                SendTimeout = (int)Timeout.TotalMilliseconds,
                ReceiveTimeout = (int)Timeout.TotalMilliseconds
            };
            socket.Connect(new UnixDomainSocketEndPoint(Path.Combine(runtime, "hypr", signature, ".socket.sock")));
            socket.Send("j/cursorpos"u8);
            var reply = new MemoryStream();
            var buffer = new byte[256];
            int read;
            while (reply.Length < 4096 && (read = socket.Receive(buffer)) > 0) reply.Write(buffer, 0, read);
            return ParseCursor(Encoding.UTF8.GetString(reply.ToArray()));
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Hyprland's answer to j/cursorpos: {"x": 799, "y": 693}.</summary>
    internal static (int X, int Y)? ParseCursor(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var xValue)
                && root.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out var yValue)
                    ? (xValue, yValue)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
