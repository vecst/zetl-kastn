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
    public int VisibleBucketCount { get; init; }
    public int VisibleSlipCount { get; init; }
    public int DeletedSlipCount { get; init; }
    public DateTimeOffset? LastActivityUtc { get; init; }
    public string PreviewText { get; init; } = "";
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

    // The view document id this project renders with by default (null = first view).
    public string? DefaultViewId { get; init; }

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
    public ZetlPictureSnapshot? Picture { get; init; }
    public ZetlCaptureOriginSnapshot? CaptureOrigin { get; init; }
    public required string Source { get; init; }
    public string? SessionId { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public string? DeletedFromBucketId { get; init; }
    public DateTimeOffset? DeletedAtUtc { get; init; }
}

public sealed record ZetlPictureSnapshot
{
    public string? SourceUrl { get; init; }
    public string MimeType { get; init; } = "image/png";
    public int Width { get; init; }
    public int Height { get; init; }
    public long ByteLength { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record ZetlPictureContent
{
    public required string SlipId { get; init; }
    public required string Sha256 { get; init; }
    public string MimeType { get; init; } = "image/png";
    public int Width { get; init; }
    public int Height { get; init; }
    public required byte[] Bytes { get; init; }
}

public sealed record ZetlCaptureOriginSnapshot
{
    public string ApplicationName { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string? WindowTitle { get; init; }
}
