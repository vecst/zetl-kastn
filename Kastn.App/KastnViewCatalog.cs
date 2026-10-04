using ZETL;
using ZETL.Contracts;

namespace KASTN;

// Project views shadow global documents by ID. Ordinary slip snapshots reuse
// the catalog; explicit authoring actions reload the shared disk store.
internal sealed class KastnViewCatalog
{
    private string? signature;
    private Dictionary<string, ZetlViewDocument> byId = new(StringComparer.Ordinal);
    private HashSet<string> projectIds = new(StringComparer.Ordinal);

    public KastnViewCatalog(ZetlViewStore store) => Store = store;
    public ZetlViewStore Store { get; }
    public IReadOnlyList<ZetlViewDocument> Global { get; private set; } = ZetlViewDefaults.CreateAll();
    public IReadOnlyList<ZetlViewDocument> Views { get; private set; } = ZetlViewDefaults.CreateAll();

    public bool Refresh(ZetlProjectSnapshot? project, bool force = false)
    {
        var next = project is null ? "global" : $"{project.Id}|{project.MetadataRevision}";
        if (!force && signature == next) return false;
        Global = Store.LoadAll();
        var local = project?.Views.Select(ZetlProjectSnapshotMapper.ToDocument).ToArray() ?? [];
        projectIds = local.Select(view => view.Id).ToHashSet(StringComparer.Ordinal);
        Views = Global.Where(view => !projectIds.Contains(view.Id)).Concat(local).ToArray();
        byId = Views.ToDictionary(view => view.Id, StringComparer.Ordinal);
        signature = next;
        return true;
    }

    public ZetlViewDocument? Find(string? id) => id is not null && byId.TryGetValue(id, out var view) ? view : null;
    public bool IsProjectScoped(string id) => projectIds.Contains(id);
    public ZetlViewDocument Select(string? id) => Find(id) ?? Views.First();
    public ZetlViewDocument SelectDefault(ZetlProjectSnapshot project, string? globalDefault) =>
        Find(project.DefaultViewId) ?? Find(globalDefault) ?? Views.First();
}
