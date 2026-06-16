namespace ZETL.Contracts;

public enum ZetlSlipType
{
    Text,
    Url,
    Picture,
    File
}

public sealed record ZetlProjectSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required long MetadataRevision { get; init; }
    public required long ChangeSequence { get; init; }
    public int BucketCount { get; init; }
    public int SlipCount { get; init; }
}

public sealed record ZetlProjectSnapshot
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    // Changes only when project-level metadata changes. Capturing a slip does
    // not make an unrelated project rename conflict.
    public required long MetadataRevision { get; init; }

    // Advances after every durable project mutation and orders notifications.
    public required long ChangeSequence { get; init; }

    public string? ActiveBucketId { get; init; }
    public IReadOnlyList<ZetlBucketSnapshot> Buckets { get; init; } = [];
    public IReadOnlyList<ZetlSlipSnapshot> Slips { get; init; } = [];
}

public sealed record ZetlBucketSnapshot
{
    public required string Id { get; init; }
    public required long Revision { get; init; }
    public required string Name { get; init; }
    public string? ParentBucketId { get; init; }
    public ZetlBucketSettings Settings { get; init; } = new();
}

public sealed record ZetlBucketSettings
{
    public string Kind { get; init; } = "Standard";
    public string DefaultKind { get; init; } = "Standard";
    public string DefaultCompileMode { get; init; } = "Formatted";
    public string DefaultStartingText { get; init; } = "";
    public int DefaultTsvRowLength { get; init; } = 5;
    public bool PopMode { get; init; }
    public string? ReplayReviewBucketId { get; init; }
}

public sealed record ZetlSlipSnapshot
{
    public required string Id { get; init; }
    public required long Revision { get; init; }
    public required ZetlSlipType Type { get; init; }
    public required string BucketId { get; init; }
    public required string Text { get; init; }
    public required string Source { get; init; }
    public string? SessionId { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public string? DeletedFromBucketId { get; init; }
    public DateTimeOffset? DeletedAtUtc { get; init; }
}
