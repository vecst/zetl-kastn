using System.Text.Json;

namespace ZETL.Contracts;

public enum ZetlIpcMessageKind
{
    Hello,
    Welcome,
    Command,
    Response,
    ProjectChanged,
    Error
}

public sealed record ZetlIpcMessage
{
    public int ProtocolVersion { get; init; } = ZetlProtocol.CurrentVersion;
    public required ZetlIpcMessageKind Kind { get; init; }
    public string? CorrelationId { get; init; }
    public JsonElement? Payload { get; init; }

    public static ZetlIpcMessage Create<T>(
        ZetlIpcMessageKind kind,
        T payload,
        string? correlationId = null)
    {
        return new ZetlIpcMessage
        {
            Kind = kind,
            CorrelationId = correlationId,
            Payload = ZetlProtocolJson.ToElement(payload)
        };
    }
}

public sealed record ZetlIpcHello
{
    public required string ClientName { get; init; }
    public required string ClientInstanceId { get; init; }
    public bool SubscribeToProjectChanges { get; init; } = true;
}

public sealed record ZetlIpcWelcome
{
    public required string ServerInstanceId { get; init; }
    public required int ProtocolVersion { get; init; }
}
