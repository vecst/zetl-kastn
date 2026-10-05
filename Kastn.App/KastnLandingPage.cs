using System.Collections.ObjectModel;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal sealed record KastnLandingCatalogs(
    Func<IReadOnlyList<ZetlTemplateDocument>> Templates,
    Func<IReadOnlyList<ZetlCreationTypeDocument>> Creations,
    Func<IReadOnlyList<ZetlViewDocument>> Views);

internal sealed record KastnLandingPresentation(bool Choices, KastnLandingSection Section, bool Online,
    bool HasRecents, bool HasProjects, bool Archived, bool Consumable)
{
    public bool Projects => Section == KastnLandingSection.Projects;
    public bool Templates => Section == KastnLandingSection.Templates;
    public bool Creations => Section == KastnLandingSection.Creations;
    public string Title => Creations ? "Create" : Templates ? "Templates" : "Projects";
    public string Subtitle => Creations ? "Start from a saved creation type."
        : Templates ? "Start a project from a reusable template." : "Open a project or manage the library.";
}

// Owns landing state, catalog reload policy and project/card projections. The
// window binds read-only collections and applies visibility, layout and input.
// Catalog visits read disk again; unchanged cards do not churn bound controls.
internal sealed class KastnLandingPage
{
    private const int RecentLimit = 10;
    private readonly KastnLandingCatalogs catalogs;
    private readonly Func<string, string> laneLabel;
    private readonly ObservableCollection<KastnLaneCard> lanes = [];
    private readonly ObservableCollection<KastnProjectCard> recentProjects = [];
    private readonly ObservableCollection<KastnProjectCard> projects = [];
    private readonly ObservableCollection<KastnTemplateCard> templates = [];
    private readonly ObservableCollection<KastnCreationCard> creations = [];
    private IReadOnlyList<ZetlProjectSummary> summaries = [];
    private Dictionary<string, KastnProjectCard> allProjects = new(StringComparer.Ordinal);
    private HashSet<string> projectNames = new(StringComparer.OrdinalIgnoreCase);
    private long actionVersion;
    private long projectActionGeneration;
    private bool suppressSelection;
    private bool retired;

    public KastnLandingPage(KastnLandingCatalogs catalogs, Func<string, string> laneLabel)
    {
        this.catalogs = catalogs;
        this.laneLabel = laneLabel;
        Lanes = new(lanes);
        RecentProjects = new(recentProjects);
        Projects = new(projects);
        Templates = new(templates);
        Creations = new(creations);
    }

    public ReadOnlyObservableCollection<KastnLaneCard> Lanes { get; }
    public ReadOnlyObservableCollection<KastnProjectCard> RecentProjects { get; }
    public ReadOnlyObservableCollection<KastnProjectCard> Projects { get; }
    public ReadOnlyObservableCollection<KastnTemplateCard> Templates { get; }
    public ReadOnlyObservableCollection<KastnCreationCard> Creations { get; }
    public KastnLandingSection Section { get; private set; }
    public bool ShowingArchived { get; private set; }
    public bool ShowingConsumable { get; private set; }

    public void Visit(KastnLandingSection section, bool? consumable = null)
    {
        if (retired) return;
        Section = section;
        if (consumable is { } type) ShowingConsumable = type;
        if (section == KastnLandingSection.Templates) ReloadTemplates();
        else if (section == KastnLandingSection.Creations) ReloadCreations();
    }

    public void SetTemplateType(bool consumable)
    {
        if (retired) return;
        ShowingConsumable = consumable;
        ReloadTemplates();
    }

    public bool SetArchived(bool archived)
    {
        if (retired || ShowingArchived == archived) return false;
        ShowingArchived = archived;
        RebuildProjects();
        return true;
    }

    public void UpdateProjects(IReadOnlyList<ZetlProjectSummary> next)
    {
        if (retired) return;
        summaries = next;
        projectNames = next.Select(project => project.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        RebuildProjects();
    }

    public bool ProjectNameExists(string name) => projectNames.Contains(name.Trim());

    public bool OwnsProjectCard(KastnProjectCard card) => !retired
        && card.ActionGeneration == projectActionGeneration
        && allProjects.TryGetValue(card.Id, out var current) && ReferenceEquals(current, card);

    public void ResetProjectActions() { projectActionGeneration++; }

    private void RebuildProjects()
    {
        var next = new Dictionary<string, KastnProjectCard>(StringComparer.Ordinal);
        foreach (var summary in summaries)
        {
            var card = CreateProjectCard(summary) with { ActionGeneration = projectActionGeneration };
            next.Add(card.Id, allProjects.TryGetValue(card.Id, out var previous) && previous == card ? previous : card);
        }
        allProjects = next;
        var cards = next.Values.ToArray();
        var laneCards = new[] { Lane(cards, ZetlStateRules.NormalLane), Lane(cards, ZetlStateRules.ShiftLane) };
        Sync(lanes, laneCards);
        var pinned = laneCards.SelectMany(card => new[] { card.Project?.Id, card.OverlayProject?.Id })
            .Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        Sync(projects, cards.Where(card => !card.IsTemporary && !pinned.Contains(card.Id) && card.IsArchived == ShowingArchived)
            .OrderBy(card => card.Name, StringComparer.OrdinalIgnoreCase).ToArray());
        Sync(recentProjects, cards.Where(card => !card.IsTemporary && !card.IsArchived)
            .OrderByDescending(card => card.LastActivityUtc ?? DateTimeOffset.MinValue)
            .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase).Take(RecentLimit).ToArray());
    }

    private KastnLaneCard Lane(IReadOnlyList<KastnProjectCard> cards, string lane)
    {
        var overlay = cards.FirstOrDefault(card => card.IsTemporary && card.ActiveLane == lane);
        var project = cards.FirstOrDefault(card => !card.IsTemporary
            && (card.ActiveLane == lane || overlay is not null && card.UnderlyingLane == lane));
        return new(lane, laneLabel(lane), project, overlay);
    }

    public void ReloadTemplates()
    {
        if (retired) return;
        var type = ShowingConsumable ? ZetlTemplateTypes.Consumable : ZetlTemplateTypes.Capture;
        var previous = templates.ToDictionary(card => card.Source.Id, StringComparer.Ordinal);
        var next = catalogs.Templates().Where(document => document.Type == type).Select(document =>
        {
            if (previous.TryGetValue(document.Id, out var card)
                && SameDocument(card.Source, document)) return card;
            return new KastnTemplateCard(document.Category, document.Name, document.Description, document,
                !ZetlTemplateDefaults.IsBuiltIn(document.Id));
        }).ToArray();
        Sync(templates, next);
    }

    public void ReloadCreations()
    {
        if (retired) return;
        var loaded = catalogs.Creations();
        var templateNames = catalogs.Templates().ToDictionary(document => document.Id, document => document.Name, StringComparer.Ordinal);
        // Read the global view store on visit too, so external renames/deletions
        // do not leave creation cards using a cached project view catalog.
        var viewNames = catalogs.Views().ToDictionary(document => document.Id, document => document.Name, StringComparer.Ordinal);
        var previous = creations.ToDictionary(card => card.Source.Id, StringComparer.Ordinal);
        var next = loaded.Select(document =>
        {
            var templateName = templateNames.GetValueOrDefault(document.TemplateId, document.TemplateId);
            var viewName = document.PrimaryViewId is { } viewId ? viewNames.GetValueOrDefault(viewId, viewId) : "no view";
            var detail = $"Template: {templateName}  ·  View: {viewName}";
            if (previous.TryGetValue(document.Id, out var card) && card.Detail == detail && SameDocument(card.Source, document)) return card;
            return new KastnCreationCard(document.Category, document.Name, detail, document,
                !ZetlCreationTypeDefaults.IsBuiltIn(document.Id));
        }).ToArray();
        Sync(creations, next);
    }

    public KastnLandingPresentation Presentation(bool choices, bool online) => new(!retired && choices, Section,
        !retired && online, recentProjects.Count > 0, projects.Count > 0, ShowingArchived, ShowingConsumable);

    public long BeginCardAction()
    {
        suppressSelection = !retired;
        return ++actionVersion;
    }
    public void EndCardAction(long version) { if (version == actionVersion) suppressSelection = false; }
    public bool ConsumeSuppressedSelection()
    {
        if (!suppressSelection) return false;
        suppressSelection = false;
        return true;
    }
    public void Retire() { retired = true; suppressSelection = false; actionVersion++; }

    private static bool SameDocument<T>(T first, T second) where T : class =>
        KastnCatalogEditorSession<T>.Fingerprint(first) == KastnCatalogEditorSession<T>.Fingerprint(second);

    private static void Sync<T>(ObservableCollection<T> current, IReadOnlyList<T> desired)
    {
        // Indexed replacements keep this linear even when sorting reverses a
        // large library. Never reset the collection for an unchanged projection.
        for (var i = current.Count - 1; i >= desired.Count; i--) current.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i >= current.Count) current.Add(desired[i]);
            else if (!EqualityComparer<T>.Default.Equals(current[i], desired[i])) current[i] = desired[i];
        }
    }

    private static KastnProjectCard CreateProjectCard(ZetlProjectSummary project)
    {
        var temporary = project.Kind == ZetlStateRules.TemporaryConsumableProjectKind;
        var detail = $"{project.VisibleSlipCount} slip{Plural(project.VisibleSlipCount)} | {project.VisibleBucketCount} bucket{Plural(project.VisibleBucketCount)}";
        if (temporary) detail = $"Temporary | {detail}";
        if (project.DeletedSlipCount > 0) detail += $" | {project.DeletedSlipCount} deleted";
        return new(project.Id, project.Name, project.MetadataRevision, detail,
            string.IsNullOrWhiteSpace(project.PreviewText) ? "No slips yet" : project.PreviewText,
            project.LastActivityUtc is null ? "No activity yet" : $"Last slip {project.LastActivityUtc.Value.LocalDateTime:g}",
            project.LastActivityUtc, project.Status, project.VisibleSlipCount, project.ActiveLane, project.UnderlyingLane,
            project.CanCreateTemporaryFromReplay, temporary);
    }
    internal static string Plural(int count) => count == 1 ? "" : "s";
}
