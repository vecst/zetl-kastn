namespace ZETL.Contracts;

public sealed record ListProjectsCommand;

public sealed record GetProjectCommand;

public sealed record GetSlipPictureCommand;

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

public sealed record SetProjectViewCommand
{
    // The default view document id, or null to clear it (fall back to the first view).
    public string? ViewId { get; init; }
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
    public string? Title { get; init; }
    public required string Text { get; init; }
    public required string Source { get; init; }
    public string? SessionId { get; init; }
    public DateTimeOffset? CapturedAtUtc { get; init; }
}

public sealed record UpdateSlipCommand
{
    // Null preserves the current title; empty clears an explicit title.
    public string? Title { get; init; }
    public required string Text { get; init; }

    // Null preserves the current value; true holds the slip out of Kastn's
    // rendered views and exports, false includes it.
    public bool? ExcludedFromViews { get; init; }

    // Null preserves the current alignment; "left" / "center" / "right" sets it.
    public string? Align { get; init; }
}

public sealed record MoveSlipCommand
{
    public required string DestinationBucketId { get; init; }
}

public sealed record ReorderSlipCommand
{
    /// <summary>
    /// The slip the target should be placed immediately before, within the same
    /// bucket. A null anchor moves the target to the end of its bucket.
    /// </summary>
    public string? BeforeSlipId { get; init; }
}

public sealed record DeleteSlipCommand;
