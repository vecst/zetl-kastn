using ZETL;

namespace KASTN;

// Selects the lower library; lanes and recents remain above it.
internal enum KastnLandingSection
{
    Projects,
    Templates,
    Creations
}

internal sealed record KastnProjectCard(
    string Id,
    string Name,
    long MetadataRevision,
    string Detail,
    string PreviewText,
    string ActivityText,
    DateTimeOffset? LastActivityUtc,
    string Status,
    int VisibleSlipCount,
    string ActiveLane,
    string UnderlyingLane,
    bool CanCreateTemporaryFromReplay,
    bool IsTemporary)
{
    public bool IsActive =>
        string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase);

    public bool IsArchived =>
        string.Equals(Status, "Archived", StringComparison.OrdinalIgnoreCase);

    // The badge only appears for non-Active projects, so Active shows nothing.
    public bool ShowStatusBadge => !IsActive;

    // One contextual status action per card: seal/put-away an active project,
    // or bring a finished/archived one back.
    public string StatusActionLabel => IsActive
        ? "Archive"
        : IsArchived ? "Unarchive" : "Reactivate";

    public string StatusActionTarget => IsActive ? "Archived" : "Active";

    public bool CanUseTemporarily => IsArchived && CanCreateTemporaryFromReplay;

    public bool CanSetActive =>
        IsActive && !string.Equals(ActiveLane, ZetlStateRules.NormalLane, StringComparison.Ordinal);

    public bool CanSetAlternateActive =>
        IsActive && !string.Equals(ActiveLane, ZetlStateRules.ShiftLane, StringComparison.Ordinal);

    public string SetActiveActionLabel =>
        string.Equals(ActiveLane, ZetlStateRules.NormalLane, StringComparison.Ordinal)
            ? "Active"
            : "Set Active";
}

internal sealed record KastnLaneCard(
    string Lane,
    string Label,
    KastnProjectCard? Project,
    KastnProjectCard? OverlayProject)
{
    public bool HasProject => Project is not null;
    public bool HasOverlay => OverlayProject is not null;
    public bool IsEmpty => Project is null;
    public string ProjectName => Project?.Name ?? $"No {Label} project";
    public string ProjectDetail => Project?.Detail ?? "Select or create a project to keep here.";
    public string ProjectPreview => Project?.PreviewText ?? "This lane is empty.";
    public string ProjectActivity => Project?.ActivityText ?? "";
    public string EmptyTitle => $"{Label} is empty";
    public string EmptyDetail => "Choose a project from the library to keep this lane ready.";
    public string OverlayName => OverlayProject?.Name ?? "";
    public string OverlayDetail => OverlayProject?.Detail ?? "";
    public string OverlayProgress => OverlayProject is null
        ? ""
        : $"{OverlayProject.VisibleSlipCount} replay item{KastnLandingPage.Plural(OverlayProject.VisibleSlipCount)} left";
}

internal sealed record KastnTemplateCard(
    string Kind,
    string Name,
    string Detail,
    ZetlTemplateDocument Source,
    bool IsUser);

internal sealed record KastnCreationCard(
    string Kind,
    string Name,
    string Detail,
    ZetlCreationTypeDocument Source,
    bool IsUser);

