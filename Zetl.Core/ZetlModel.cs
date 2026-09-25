using System.Runtime.CompilerServices;
using ZETL.Contracts;

namespace ZETL;

internal sealed class ZetlState
{
    public int Version { get; set; } = 1;
    public string? ActiveProjectId { get; set; }
    public string? ShiftActiveProjectId { get; set; }
    // The journal-mode project each lane falls back to as the default capture home.
    // Tracked by id (not name) so renaming the Journal never loses the pointer.
    public string? DefaultJournalProjectId { get; set; }
    public string? ShiftDefaultJournalProjectId { get; set; }
    // The last non-journal project that was lane-active, so the held Ctrl+A toggle can
    // jump back to it from the Journal.
    public string? LastDeliberateProjectId { get; set; }
    public string? ShiftLastDeliberateProjectId { get; set; }
    public List<ZetlProject> Projects { get; set; } = new();
}

// Result of the held Ctrl+A active-project toggle.
internal enum ZetlProjectToggleOutcome
{
    Activated,
    ReturnedToJournal,
    NoProjectToActivate
}

internal sealed class ZetlProject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long MetadataRevision { get; set; } = 1;
    public long ChangeSequence { get; set; }
    // Lifecycle status: "Active" (default), "Finished", or "Archived". Distinct
    // from lane-active state, which lives in the workspace pointers. Reversible.
    public string Status { get; set; } = ZetlStateStore.ActiveStatus;
    // Project kind: Standard projects are durable; TemporaryConsumable projects
    // are deleted when they stop occupying their assigned active lane.
    public string Kind { get; set; } = ZetlStateStore.StandardProjectKind;
    public string? SourceTemplateId { get; set; }
    public string? TemporaryLane { get; set; }
    // Journal mode: one rolling project whose buckets are the days of the period
    // (pre-seeded, named e.g. "Mon 07-06"). Capture routes into today's day parent —
    // copy into its "Capture" child, quick notes into its "Quick Note" child — so the
    // project reads as a dated journal. The day boundary is the DayStartHour setting.
    public bool JournalMode { get; set; }
    // Session-scoped activity stamp (not persisted) for the auto-return-to-Journal
    // check: refreshed when a deliberate project is activated or captured into. A
    // restart simply leaves it unset, so auto-return waits for fresh activity.
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime LastActiveUtc { get; set; }
    public string? ActiveBucketId { get; set; }
    public string? QuickNoteBucketId { get; set; }
    // The view document this project renders with by default (set by a creation
    // type, or chosen in Kastn). Null falls back to the first view.
    public string? DefaultViewId { get; set; }
    // Structured views are project-owned so project export/backup carries them.
    // Universal views remain in the global view catalog.
    public List<ZetlViewDocument> Views { get; set; } = [];
    public List<ZetlBucket> Buckets { get; set; } = new();

    // Board project-picker label: the plain name for an Active project, with a
    // trailing status marker for a Finished/Archived one so the picker reads
    // clearly without a value converter.
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayNameWithStatus =>
        string.Equals(Status, ZetlStateStore.ActiveStatus, StringComparison.OrdinalIgnoreCase)
            ? Name
            : $"{Name}  ·  {Status}";
}

internal sealed class ZetlBucketSettings
{
    public string Kind { get; set; } = "Standard";
    public string DefaultKind { get; set; } = "Standard";
    public string DefaultCompileMode { get; set; } = "Formatted";
    public string DefaultStartingText { get; set; } = "";
    public int DefaultTsvRowLength { get; set; } = 5;
    public bool PopMode { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("fifoReviewBucketId")]
    public string? ReplayReviewBucketId { get; set; }
    public string? PopReviewBucketId { get; set; }
}

internal sealed class ZetlBucket
{
    public string Id { get; set; } = "";
    public long Revision { get; set; } = 1;
    public string Name { get; set; } = "";
    public string? ParentBucketId { get; set; }
    
    public ZetlBucketSettings Settings { get; set; } = new();

    // Legacy flat properties for backward compatibility on deserialization:
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public string? LegacyKind { set { if (value is not null) Settings.Kind = value; } }
    [System.Text.Json.Serialization.JsonPropertyName("defaultKind")]
    public string? LegacyDefaultKind { set { if (value is not null) Settings.DefaultKind = value; } }
    [System.Text.Json.Serialization.JsonPropertyName("defaultCompileMode")]
    public string? LegacyDefaultCompileMode { set { if (value is not null) Settings.DefaultCompileMode = value; } }
    [System.Text.Json.Serialization.JsonPropertyName("defaultStartingText")]
    public string? LegacyDefaultStartingText { set { if (value is not null) Settings.DefaultStartingText = value; } }
    [System.Text.Json.Serialization.JsonPropertyName("defaultTsvRowLength")]
    public int? LegacyDefaultTsvRowLength { set { if (value is not null) Settings.DefaultTsvRowLength = value.Value; } }
    [System.Text.Json.Serialization.JsonPropertyName("popMode")]
    public bool? LegacyPopMode { set { if (value is not null) Settings.PopMode = value.Value; } }
    [System.Text.Json.Serialization.JsonPropertyName("fifoReviewBucketId")]
    public string? LegacyReplayReviewBucketId { set { if (value is not null) Settings.ReplayReviewBucketId = value; } }

    // Per-bucket heading styling for rendered views (the bucket's title in the
    // all-buckets layouts). Align "" (left) / "center" / "right"; Bold; Level 1/2/3
    // sizes the heading (0 = automatic). Honored by HTML/PDF/on-screen; Markdown
    // uses only the level. A custom view section can still override these.
    public string HeadingAlign { get; set; } = "";
    public bool HeadingBold { get; set; }
    public int HeadingLevel { get; set; }
    // Kastn-only: how Kastn renders this bucket's contents — "" (a normal section),
    // "group" (a boxed labeled container), "table", or "latex". Zetl treats the bucket
    // as ordinary; only Kastn's renderers read this.
    public string RenderKind { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("notes")]
    public List<ZetlSlip> Slips { get; set; } = new();

    // Transitional source alias. Persisted JSON is owned by Slips above.
    [System.Text.Json.Serialization.JsonIgnore]
    public List<ZetlSlip> Notes
    {
        get => Slips;
        set => Slips = value;
    }
}

internal sealed record BucketDisplayItem(ZetlBucket Bucket, string Label)
{
    public override string ToString()
    {
        return Label;
    }
}

internal sealed record SlipDisplayItem(ZetlBucket Bucket, ZetlSlip Slip, string Label)
{
    // Transitional member alias for callers not yet migrated.
    public ZetlSlip Note => Slip;

    public override string ToString()
    {
        return Label;
    }
}

internal sealed class ZetlSlip
{
    public string Id { get; set; } = "";
    public long Revision { get; set; } = 1;
    private ZetlSlipType type = ZetlSlipType.Text;
    public ZetlSlipType Type
    {
        get => type;
        set => type = value;
    }
    public string Title { get; set; } = "";

    private string text = "";
    public string Text
    {
        get => text;
        set
        {
            text = value;
            if (type == ZetlSlipType.Text && ZetlSlipClassifier.LooksLikeUrl(text))
            {
                type = ZetlSlipType.Url;
            }
            else if (type == ZetlSlipType.Url && !ZetlSlipClassifier.LooksLikeUrl(text))
            {
                type = ZetlSlipType.Text;
            }
        }
    }

    private ZetlImageAsset? image;
    public ZetlImageAsset? Image
    {
        get => image;
        set
        {
            image = value;
            // Only an image-only slip is forced to Picture. A slip with text
            // content keeps its type: Type is the preferred representation of a
            // dual (text + picture) capture, and JSON populates Text before
            // Image, so this must not override a persisted Text preference.
            if (image is not null)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    type = ZetlSlipType.Picture;
                }
            }
            else if (type == ZetlSlipType.Picture)
            {
                type = ZetlSlipClassifier.LooksLikeUrl(Text) ? ZetlSlipType.Url : ZetlSlipType.Text;
            }
        }
    }
    public string Source { get; set; } = "";

    // The source application's HTML clipboard representation. Zetl keeps this
    // private to the slip (it is not rendered by Kastn) so Replay can reproduce
    // rich text such as spreadsheet emphasis and alignment.
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RichHtml { get; set; }

    // Allowlisted native source formats used only to reproduce a Replay paste.
    // RegisteredName lets Windows resolve session-local registered format IDs
    // again after a reboot.
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<ZetlClipboardFormatData>? ReplayFormats { get; set; }

    public string? SessionId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? DeletedFromBucketId { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("contentKind")]
    public string? LegacyContentKind
    {
        set
        {
            if (value is not null)
            {
                Type = value.Equals("Image", StringComparison.OrdinalIgnoreCase) ? ZetlSlipType.Picture : ZetlSlipType.Text;
            }
        }
    }

    // Kastn-only: when true, this slip is held out of rendered views and exports
    // (a deliberate-workbench choice). It still appears in the tree and in Zetl's
    // fast compile; default false so existing slips load as included.
    public bool ExcludedFromViews { get; set; }

    // Kastn-only: per-slip block alignment for rendered views — "center" or
    // "right". Null/absent means left (the default), so existing slips load
    // unchanged and a left slip writes no field.
    public string? Align { get; set; }

    // Kastn-only: the note's own block kind in rendered views — a list item
    // ("bullet"/"ordered"/"task") or a structural leaf ("divider"); "" (the default)
    // renders as a plain paragraph. The JSON key stays "listKind" (the field's original
    // name) so existing projects keep loading their kinds after the rename to BlockKind.
    [System.Text.Json.Serialization.JsonPropertyName("listKind")]
    public string BlockKind { get; set; } = "";

    // Kastn-only: when true, bucket RenderKind inheritance is suppressed for this
    // slip. The slip's own BlockKind still renders normally.
    public bool IgnoreBucketRenderKind { get; set; }

    // Kastn-only: checked state for a "task" note; ignored for other kinds.
    public bool Checked { get; set; }

    // Kastn-only: whole-slip text styles for rendered views. Default false so
    // existing slips load unstyled and an unstyled slip writes no field.
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Strike { get; set; }

    // Kastn-only: optional whole-slip typography. Empty strings / size 0 inherit
    // the active theme and keep old project files behaviorally unchanged.
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string FontFamily { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int FontSize { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string TextColor { get; set; } = "";

    // Kastn-only: property-backed inline styling over Text.
    public List<ZetlInlineStyleRange> InlineStyles { get; set; } = [];

    public ZetlCaptureOrigin? CaptureOrigin { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCaptureOrigin => CaptureOrigin?.HasDisplayValue == true;

    [System.Text.Json.Serialization.JsonIgnore]
    public string CaptureOriginLabel => CaptureOrigin?.FormatDisplay(CreatedAtUtc) ?? "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsImage => Type == ZetlSlipType.Picture && Image is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayText => IsImage
        ? string.IsNullOrWhiteSpace(Text)
            ? !string.IsNullOrWhiteSpace(Title)
                ? Title
                : $"Image · {Image!.Width}×{Image.Height} · {FormatBytes(Image.ByteLength)}"
            : Text
        : string.IsNullOrWhiteSpace(Title) ? Text : Title;

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024d):0.#} MB"
        : $"{Math.Max(1, bytes / 1024d):0.#} KB";
}

internal sealed class ZetlImageAsset
{
    public string RelativePath { get; set; } = "";
    public string? SourceUrl { get; set; }
    public string MimeType { get; set; } = "image/png";
    public int Width { get; set; }
    public int Height { get; set; }
    public long ByteLength { get; set; }
    public string Sha256 { get; set; } = "";
}

// A bucket's complete authored definition, as the project service applies it
// on add or update. A null RenderKind keeps the bucket's current render kind.
internal sealed record ZetlBucketDefinition(
    string Name,
    string? ParentBucketId,
    string Kind,
    string DefaultKind,
    string DefaultCompileMode,
    string DefaultStartingText,
    int DefaultTsvRowLength,
    bool PopMode,
    string? ReplayReviewBucketId,
    string? RenderKind);

internal sealed record ZetlBucketDefaults(IReadOnlyList<string> ProjectBuckets, string CompileMode, int TsvRowLength)
{
    // The hour (0-23, local) a journal day begins, used to roll a journal-mode
    // project into the right dated bucket. 0 = midnight.
    public int DayStartHour { get; init; }

    // Hours of no capture after which a deliberate project auto-returns capture to
    // the Journal. 0 = off.
    public int JournalAutoReturnHours { get; init; }

    public string JournalInterval { get; init; } = "Weekly";

    public static ZetlBucketDefaults Standard { get; } = new(new[] { "Inbox", ZetlStateStore.ScratchBucketName }, "Formatted", 5);

    // The configured project buckets, or the built-in Inbox/Scratch fallback
    // when none are set. Centralizes the fallback several call sites inlined.
    public static IReadOnlyList<string> ResolveProjectBuckets(IReadOnlyList<string>? buckets)
    {
        return buckets is { Count: > 0 } ? buckets : Standard.ProjectBuckets;
    }

    public IReadOnlyList<string> ResolvedProjectBuckets => ResolveProjectBuckets(ProjectBuckets);
}
