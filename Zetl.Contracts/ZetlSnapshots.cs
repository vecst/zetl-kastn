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

    // Lifecycle status: "Active" (default), "Finished", or "Archived".
    public string Status { get; init; } = "Active";
    public string Kind { get; init; } = "Standard";
    public string? SourceTemplateId { get; init; }
    public string? TemporaryLane { get; init; }
    public string ActiveLane { get; init; } = "";
    public string UnderlyingLane { get; init; } = "";
    public int BucketCount { get; init; }
    public int SlipCount { get; init; }
    public int VisibleBucketCount { get; init; }
    public int VisibleSlipCount { get; init; }
    public int DeletedSlipCount { get; init; }
    public bool CanCreateTemporaryFromReplay { get; init; }
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

    // Lifecycle status: "Active" (default), "Finished", or "Archived". Orthogonal
    // to lane-active state (which lives in the workspace pointers).
    public string Status { get; init; } = "Active";
    public string Kind { get; init; } = "Standard";
    public string? SourceTemplateId { get; init; }
    public string? TemporaryLane { get; init; }

    // Journal mode: capture rolls into a fresh per-day bucket instead of a fixed
    // active bucket, so the project reads as a dated journal.
    public bool JournalMode { get; init; }

    // The view document id this project renders with by default (null = first view).
    public string? DefaultViewId { get; init; }

    // Structured views authored specifically for this project. Universal views
    // remain in Kastn's global catalog and are not duplicated here.
    public IReadOnlyList<ZetlProjectViewSnapshot> Views { get; init; } = [];

    public IReadOnlyList<ZetlBucketSnapshot> Buckets { get; init; } = [];
    public IReadOnlyList<ZetlSlipSnapshot> Slips { get; init; } = [];
}

public sealed record ZetlProjectViewSnapshot
{
    public int Version { get; init; } = 1;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Category { get; init; } = "Custom";
    public string Description { get; init; } = "";
    public string Kind { get; init; } = "Formatted";
    public int TsvRowLength { get; init; } = 5;
    public IReadOnlyList<ZetlProjectViewSectionSnapshot> Sections { get; init; } = [];
    public string ListStyle { get; init; } = "bullet";
    public bool NumberHeadings { get; init; }

    // Document-title control for the rich kinds: false hides the title; a non-empty
    // Title overrides the project name.
    public bool ShowTitle { get; init; } = true;
    public string Title { get; init; } = "";
}

public sealed record ZetlProjectViewSectionSnapshot
{
    public required string Title { get; init; }
    public IReadOnlyList<string> Buckets { get; init; } = [];

    // Heading styling: align ("" / "center" / "right"), bold, and level (1/2/3 size;
    // 0 = automatic). Honored by HTML / PDF / on-screen; Markdown uses only the level.
    public string HeadingAlign { get; init; } = "";
    public bool HeadingBold { get; init; }
    public int HeadingLevel { get; init; }
}

public sealed record ZetlBucketSnapshot
{
    public required string Id { get; init; }
    public required long Revision { get; init; }
    public required string Name { get; init; }
    public string? ParentBucketId { get; init; }
    public ZetlBucketSettings Settings { get; init; } = new();

    // Per-bucket heading styling for rendered views: align ("" / "center" / "right"),
    // bold, and level (1/2/3 size; 0 = automatic). HTML/PDF/on-screen honor all
    // three; Markdown uses only the level. Set through SetBucketHeading.
    public string HeadingAlign { get; init; } = "";
    public bool HeadingBold { get; init; }
    public int HeadingLevel { get; init; }

    // Kastn-only: how Kastn renders this bucket's contents — "" (normal section),
    // "group" (boxed container), "table", or "latex". Zetl ignores it.
    public string RenderKind { get; init; } = "";
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
    public string Title { get; init; } = "";
    public required string Text { get; init; }
    public ZetlPictureSnapshot? Picture { get; init; }
    public ZetlCaptureOriginSnapshot? CaptureOrigin { get; init; }
    public required string Source { get; init; }
    public string? SessionId { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public string? DeletedFromBucketId { get; init; }
    public DateTimeOffset? DeletedAtUtc { get; init; }

    // Kastn-only: true holds the slip out of rendered views and exports.
    public bool ExcludedFromViews { get; init; }

    // Kastn-only: per-slip block alignment for rendered views — "center" or
    // "right"; null/absent = left.
    public string? Align { get; init; }

    // Kastn-only: the note's own list-item kind in rendered views — "bullet",
    // "ordered", or "task"; "" / absent renders as a plain paragraph. Authoritative
    // per note (it is not inherited from a view-wide style).
    public string BlockKind { get; init; } = "";

    // Kastn-only: when true, this slip ignores its bucket's inherited render kind.
    public bool IgnoreBucketRenderKind { get; init; }

    // Kastn-only: the checked state when BlockKind is "task"; ignored otherwise.
    public bool Checked { get; init; }

    // Kastn-only: property-backed inline styling over Text. Typed Markdown remains
    // valid; these ranges are the toolbar/editor intent layer.
    public IReadOnlyList<ZetlInlineStyleRange> InlineStyles { get; init; } = [];
}

public sealed record ZetlInlineStyleRange
{
    public int Start { get; init; }
    public int Length { get; init; }
    public string Kind { get; init; } = "";
    public string? Href { get; init; }
    public string? TargetSlipId { get; init; }
    public string? CachedTitle { get; init; }
}

public static class ZetlInlineStyleKinds
{
    public const string Bold = "bold";
    public const string Italic = "italic";
    public const string Strike = "strike";
    public const string Code = "code";
    public const string Link = "link";
    public const string WikiLink = "wikiLink";

    public static string Normalize(string? kind) =>
        kind?.Trim().ToLowerInvariant() switch
        {
            Bold => Bold,
            Italic => Italic,
            Strike or "strikethrough" => Strike,
            Code or "inlinecode" or "inline-code" => Code,
            Link => Link,
            "wiki" or "wikilink" or "wiki-link" => WikiLink,
            _ => ""
        };
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
