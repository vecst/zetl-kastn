namespace ZETL.Contracts;

public sealed record ListProjectsCommand;

public sealed record GetProjectCommand;

public sealed record CreateProjectCommand
{
    public required string Name { get; init; }
    public IReadOnlyList<CreateBucketDefinition> Buckets { get; init; } = [];
}

public sealed record CreateBucketDefinition
{
    public required string Name { get; init; }
    public string? ParentBucketId { get; init; }
    public ZetlBucketSettings Settings { get; init; } = new();
}

public sealed record RenameProjectCommand
{
    public required string Name { get; init; }
}

public sealed record DeleteProjectCommand;

public sealed record AddBucketCommand
{
    public required string Name { get; init; }
    public string? ParentBucketId { get; init; }
    public ZetlBucketSettings Settings { get; init; } = new();
}

public sealed record UpdateBucketCommand
{
    public required string Name { get; init; }
    public string? ParentBucketId { get; init; }
    public ZetlBucketSettings Settings { get; init; } = new();
}

public sealed record DeleteBucketCommand;

public sealed record AddSlipCommand
{
    public required string BucketId { get; init; }
    public required string Text { get; init; }
    public required string Source { get; init; }
    public string? SessionId { get; init; }
    public DateTimeOffset? CapturedAtUtc { get; init; }
}

public sealed record UpdateSlipCommand
{
    public required string Text { get; init; }
}

public sealed record MoveSlipCommand
{
    public required string DestinationBucketId { get; init; }
}

public sealed record DeleteSlipCommand;
