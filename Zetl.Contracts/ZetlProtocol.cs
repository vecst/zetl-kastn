using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZETL.Contracts;

public static class ZetlProtocol
{
    public const int CurrentVersion = 1;
}

public static class ZetlProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static JsonElement ToElement<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, Options);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

public enum ZetlCommandKind
{
    ListProjects,
    GetProject,
    CreateProject,
    RenameProject,
    DeleteProject,
    AddBucket,
    UpdateBucket,
    DeleteBucket,
    AddSlip,
    UpdateSlip,
    MoveSlip,
    ReorderSlip,
    DeleteSlip
}

public enum ZetlResponseStatus
{
    Success,
    Conflict,
    NotFound,
    ValidationError,
    UnsupportedProtocol,
    Failure
}

public enum ZetlEntityKind
{
    Workspace,
    Project,
    Bucket,
    Slip
}

public enum ZetlChangeKind
{
    Created,
    Updated,
    Deleted
}

public sealed record ZetlCommandEnvelope
{
    public int ProtocolVersion { get; init; } = ZetlProtocol.CurrentVersion;
    public required string CommandId { get; init; }
    public required ZetlCommandKind Kind { get; init; }
    public string? ProjectId { get; init; }
    public string? TargetId { get; init; }
    public long? ExpectedTargetRevision { get; init; }
    public JsonElement? Payload { get; init; }

    public static ZetlCommandEnvelope Create<TPayload>(
        string commandId,
        ZetlCommandKind kind,
        TPayload payload,
        string? projectId = null,
        string? targetId = null,
        long? expectedTargetRevision = null)
    {
        return new ZetlCommandEnvelope
        {
            CommandId = commandId,
            Kind = kind,
            ProjectId = projectId,
            TargetId = targetId,
            ExpectedTargetRevision = expectedTargetRevision,
            Payload = ZetlProtocolJson.ToElement(payload)
        };
    }
}

public sealed record ZetlResponseEnvelope
{
    public int ProtocolVersion { get; init; } = ZetlProtocol.CurrentVersion;
    public required string CommandId { get; init; }
    public required ZetlResponseStatus Status { get; init; }
    public string? ProjectId { get; init; }
    public long? ProjectChangeSequence { get; init; }
    public JsonElement? Payload { get; init; }
    public ZetlConflict? Conflict { get; init; }
    public ZetlProtocolError? Error { get; init; }
}

public sealed record ZetlConflict
{
    public required ZetlEntityKind TargetKind { get; init; }
    public required string TargetId { get; init; }
    public required long ExpectedRevision { get; init; }
    public required long ActualRevision { get; init; }
    public required JsonElement Current { get; init; }
}

public sealed record ZetlProtocolError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public sealed record ZetlProjectChangedEvent
{
    public int ProtocolVersion { get; init; } = ZetlProtocol.CurrentVersion;
    public required string EventId { get; init; }
    public required string ProjectId { get; init; }
    public required long ProjectChangeSequence { get; init; }
    public required ZetlChangeKind ChangeKind { get; init; }
    public required ZetlEntityKind EntityKind { get; init; }
    public required string EntityId { get; init; }
    public long? EntityRevision { get; init; }
}
