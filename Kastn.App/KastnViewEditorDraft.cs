using System.Text.Json;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal sealed record KastnViewEditorContext(ZetlProjectSnapshot? Project, long Generation)
{
    public bool Matches(KastnViewEditorContext other) =>
        Project?.Id == other.Project?.Id && Generation == other.Generation;
}

internal sealed class KastnViewSectionDraft
{
    public KastnViewSectionDraft(ZetlViewSection section) => Section = JsonFile.Clone(section);
    public string Id { get; } = Guid.NewGuid().ToString("N");
    private ZetlViewSection Section { get; }
    public string Title { get => Section.Title; set => Section.Title = value; }
    public List<string> Buckets => Section.Buckets;
    public string HeadingAlign { get => Section.HeadingAlign; set => Section.HeadingAlign = value; }
    public bool HeadingBold { get => Section.HeadingBold; set => Section.HeadingBold = value; }
    public int HeadingLevel { get => Section.HeadingLevel; set => Section.HeadingLevel = value; }
    public ZetlViewSection Capture()
    {
        var result = JsonFile.Clone(Section);
        result.Title = result.Title.Trim();
        return result;
    }
}

// One editor session owns its independent fields/sections and saved baseline.
// Captures are deep copies, so an in-flight save cannot absorb later writing.
internal sealed class KastnViewEditorDraft
{
    public KastnViewEditorDraft(ZetlViewDocument document, bool projectScoped, KastnViewEditorContext context)
    {
        Document = ZetlViewDefaults.Clone(document);
        ProjectScoped = projectScoped;
        Context = context;
        savedProjectView = projectScoped ? context.Project?.Views.FirstOrDefault(view => view.Id == document.Id) : null;
        Sections.AddRange(Document.Sections.Select(section => new KastnViewSectionDraft(section)));
        CustomSections = Sections.Count > 0;
    }

    public ZetlViewDocument Document { get; }
    public KastnViewEditorContext Context { get; }
    public bool ProjectScoped { get; private set; }
    public List<KastnViewSectionDraft> Sections { get; } = [];
    public bool CustomSections { get; private set; }
    private string baseline = "";
    private ZetlProjectViewSnapshot? savedProjectView;
    public bool IsDirty => Fingerprint(Capture()) != baseline;
    public void SetBaseline() => baseline = Fingerprint(Capture());
    public void AcceptSaved(ZetlViewDocument saved)
    {
        baseline = Fingerprint(saved);
        ProjectScoped = saved.Sections.Count > 0;
        savedProjectView = ProjectScoped ? ZetlProjectSnapshotMapper.ToSnapshot(saved) : null;
    }

    public bool HasCurrentProjectView(ZetlProjectSnapshot? project) => !ProjectScoped
        || savedProjectView is not null && project?.Views.FirstOrDefault(view => view.Id == Document.Id) is { } current
            && JsonSerializer.Serialize(current, ZetlProtocolJson.Options) == JsonSerializer.Serialize(savedProjectView, ZetlProtocolJson.Options);

    public ZetlViewDocument Capture()
    {
        var result = ZetlViewDefaults.Clone(Document);
        result.Sections = CustomSections ? Sections.Select(section => section.Capture()).ToList() : [];
        return result;
    }

    public KastnViewSectionDraft AddSection(IReadOnlyList<string> bucketNames)
    {
        var used = Sections.SelectMany(section => section.Buckets).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bucket = bucketNames.FirstOrDefault(name => !used.Contains(name)) ?? bucketNames.FirstOrDefault();
        var draft = new KastnViewSectionDraft(new() { Title = bucket ?? $"Section {Sections.Count + 1}" });
        if (bucket is not null) draft.Buckets.Add(bucket);
        Sections.Add(draft);
        CustomSections = true;
        return draft;
    }

    public void SetStructureMode(bool custom, IReadOnlyList<string> buckets)
    {
        CustomSections = custom;
        if (custom && Sections.Count == 0) AddSection(buckets);
    }

    public void RemoveSection(KastnViewSectionDraft section, IReadOnlyList<string> buckets)
    {
        if (!Sections.Remove(section)) return;
        if (Sections.Count == 0) AddSection(buckets);
    }

    public void MoveSection(KastnViewSectionDraft section, int delta)
    {
        var index = Sections.IndexOf(section);
        var destination = index + delta;
        if (index < 0 || destination < 0 || destination >= Sections.Count) return;
        (Sections[index], Sections[destination]) = (Sections[destination], Sections[index]);
    }

    public bool ReorderSection(KastnViewSectionDraft source, KastnViewSectionDraft target, bool before)
    {
        var from = Sections.IndexOf(source);
        var to = Sections.IndexOf(target);
        if (from < 0 || to < 0 || source == target) return false;
        var insertion = to + (before ? 0 : 1);
        if (from < insertion) insertion--;
        if (from == insertion) return false;
        Sections.RemoveAt(from);
        Sections.Insert(insertion, source);
        return true;
    }

    public static string Fingerprint(ZetlViewDocument document) => JsonSerializer.Serialize(document, JsonFile.Options);
}
