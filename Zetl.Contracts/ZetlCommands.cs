using System.Text.Json.Serialization;

namespace ZETL.Contracts;

public sealed record ListProjectsCommand;

public sealed record GetProjectCommand;

public sealed record GetSlipPictureCommand;

public sealed record CreateProjectCommand
{
    public required string Name { get; init; }
    public string Kind { get; init; } = "Standard";
    public string? SourceTemplateId { get; init; }
    public string? TemporaryLane { get; init; }
    public bool ActivateShifted { get; init; }
    public IReadOnlyList<CreateBucketDefinition> Buckets { get; init; } = [];
}

public sealed record CreateTemporaryProjectFromReplayCommand
{
    public required string Name { get; init; }
    public required string TemporaryLane { get; init; }
    public bool ActivateShifted { get; init; }
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

public sealed record SetActiveProjectCommand
{
    // False selects the Main lane; true selects the Alternate lane.
    public bool ActivateShifted { get; init; }
}

public sealed record SetProjectStatusCommand
{
    // The lifecycle status: "Active", "Finished", or "Archived".
    public required string Status { get; init; }
}

public sealed record SetJournalModeCommand
{
    // Whether capture should roll into a fresh per-day bucket for this project.
    public required bool JournalMode { get; init; }
}

public sealed record SetProjectViewCommand
{
    // The default view document id, or null to clear it (fall back to the first view).
    public string? ViewId { get; init; }
}

public sealed record SaveProjectViewCommand
{
    public required ZetlProjectViewSnapshot View { get; init; }
}

public sealed record DeleteProjectViewCommand
{
    public required string ViewId { get; init; }
}

public sealed record DeleteProjectCommand;

public sealed record AddBucketCommand
{
    public required string Name { get; init; }
    public string? ParentBucketId { get; init; }
    public ZetlBucketSettings Settings { get; init; } = new();

    // Optional Kastn render kind for the new bucket: "group" / "table" / "latex"
    // create a structural container; "" (default) is a normal bucket.
    public string? RenderKind { get; init; }
}

public sealed record UpdateBucketCommand
{
    public required string Name { get; init; }
    public string? ParentBucketId { get; init; }
    public ZetlBucketSettings Settings { get; init; } = new();
    public string? RenderKind { get; init; }
}

public sealed record SetBucketHeadingCommand
{
    // Heading styling for the bucket's title in rendered views.
    public string Align { get; init; } = "";
    public bool Bold { get; init; }
    public int Level { get; init; }
}

public sealed record DeleteBucketCommand;

public sealed record ReorderBucketCommand
{
    /// <summary>
    /// The sibling bucket the target should be placed immediately before, under the
    /// same parent. A null anchor moves the target to the end of its sibling group.
    /// Reorder never changes a bucket's parent — that remains UpdateBucket.
    /// </summary>
    public string? BeforeBucketId { get; init; }
}

public sealed record AddSlipCommand
{
    public required string BucketId { get; init; }
    public string? Title { get; init; }
    public required string Text { get; init; }
    public required string Source { get; init; }
    public string? SessionId { get; init; }
    public DateTimeOffset? CapturedAtUtc { get; init; }

    // Optional block kind for the new note. "divider" creates a content-less structural
    // note (a rendered rule); the list/heading/quote/code kinds are normally set later.
    public string? BlockKind { get; init; }

    // Optional Kastn render override: when true, this slip ignores its bucket's
    // inherited render kind.
    public bool? IgnoreBucketRenderKind { get; init; }
}

public sealed record UpdateSlipCommand
{
    // Null preserves the current title; empty clears an explicit title.
    public string? Title { get; init; }
    public required string Text { get; init; }

    // Null preserves the current slip type. For a slip carrying both text and
    // a picture (a dual capture), Picture / Text choose the preferred
    // representation; Text is normalized to Url automatically when the body is
    // a bare link. Picture is valid only for slips that have a picture.
    public ZetlSlipType? Type { get; init; }

    // Null preserves the current value; true holds the slip out of Kastn's
    // rendered views and exports, false includes it.
    public bool? ExcludedFromViews { get; init; }

    // Null preserves the current alignment; "left" / "center" / "right" sets it.
    public string? Align { get; init; }

    // Null preserves the current list kind; "" clears it (plain paragraph) and
    // "bullet" / "ordered" / "task" set the note's own list-item kind.
    public string? BlockKind { get; init; }

    // Null preserves the current value; true makes this slip ignore inherited
    // bucket render kind, false lets the bucket render kind apply.
    public bool? IgnoreBucketRenderKind { get; init; }

    // Null preserves the current checked state; true/false set it (only meaningful
    // when the note's list kind is "task").
    public bool? Checked { get; init; }

    // Null preserves the current whole-slip style; true/false set it.
    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
    public bool? Strike { get; init; }

    // Null preserves the current whole-slip typography. Empty strings and size 0
    // explicitly clear an override so the active theme is inherited again.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FontFamily { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FontSize { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextColor { get; init; }

    // Null preserves the current inline style ranges; an empty list explicitly clears
    // them. Ranges are normalized against Text before persisting.
    public IReadOnlyList<ZetlInlineStyleRange>? InlineStyles { get; init; }
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

// Attaches or replaces a slip's picture with normalized PNG content. A slip
// with text keeps presenting as text (a dual slip); a slip without text
// presents as a picture. The prior asset file stays on disk content-addressed.
public sealed record SetSlipPictureCommand
{
    public required byte[] Bytes { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

// Detaches a slip's picture. Rejected when the slip would be left with neither
// text nor a title. A picture-presenting slip returns to text presentation.
public sealed record RemoveSlipPictureCommand;
