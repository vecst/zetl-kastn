using System.Collections.Immutable;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

// Snapshot-local results share immutable parsing and reverse-edge state. Unresolved
// edges are retained so adding a target makes old links resolve without reparsing.
internal sealed class KastnBacklinkIndex
{
    private sealed record Source(string Text, string AuthoredTitle, string Title, ImmutableHashSet<string> Targets);
    private readonly ImmutableDictionary<string, Source> sources;
    private readonly ImmutableDictionary<string, ImmutableHashSet<string>> incoming;
    private readonly IReadOnlyList<string> order;
    public IReadOnlyDictionary<string, IReadOnlyList<ZetlSlipBacklink>> Backlinks { get; }
    public int ParsedSourceCount { get; }

    private KastnBacklinkIndex(ImmutableDictionary<string, Source> sources,
        ImmutableDictionary<string, ImmutableHashSet<string>> incoming, IReadOnlyList<string> order,
        IReadOnlyDictionary<string, IReadOnlyList<ZetlSlipBacklink>> backlinks, int parsedSourceCount)
    {
        this.sources = sources;
        this.incoming = incoming;
        this.order = order;
        Backlinks = backlinks;
        ParsedSourceCount = parsedSourceCount;
    }

    public static KastnBacklinkIndex Update(ZetlProjectSnapshot project, KastnBacklinkIndex? previous,
        Func<string, int> position)
    {
        var sources = (previous?.sources ?? ImmutableDictionary<string, Source>.Empty.WithComparers(StringComparer.Ordinal)).ToBuilder();
        var incoming = (previous?.incoming ?? ImmutableDictionary<string, ImmutableHashSet<string>>.Empty.WithComparers(StringComparer.Ordinal)).ToBuilder();
        var backlinks = (previous?.Backlinks as ImmutableDictionary<string, IReadOnlyList<ZetlSlipBacklink>>
            ?? ImmutableDictionary<string, IReadOnlyList<ZetlSlipBacklink>>.Empty.WithComparers(StringComparer.Ordinal)).ToBuilder();
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var edgeChanges = new Dictionary<string, ImmutableHashSet<string>.Builder>(StringComparer.Ordinal);
        var order = new string[project.Slips.Count];
        var present = new HashSet<string>(StringComparer.Ordinal);
        var reordered = previous is null || previous.order.Count != order.Length;
        var parsed = 0;
        for (var i = 0; i < project.Slips.Count; i++)
        {
            var slip = project.Slips[i];
            order[i] = slip.Id;
            present.Add(slip.Id);
            reordered |= previous is not null && i < previous.order.Count && previous.order[i] != slip.Id;
            sources.TryGetValue(slip.Id, out var old);
            if (old is not null && old.Text == slip.Text && old.AuthoredTitle == slip.Title) continue;
            var targets = old?.Text == slip.Text ? old.Targets
                : ZetlSlipLinks.Parse(slip.Text).Select(link => link.TargetId).ToImmutableHashSet(StringComparer.Ordinal);
            if (old?.Text != slip.Text) parsed++;
            var source = new Source(slip.Text, slip.Title, ZetlSlipLinks.TitleFor(slip), targets);
            sources[slip.Id] = source;
            var titleChanged = old is null || old.Title != source.Title;
            if (old is null) affected.Add(slip.Id); // This ID may be a previously unresolved target.
            if (old is not null)
                foreach (var target in old.Targets)
                {
                    if (titleChanged || !targets.Contains(target)) affected.Add(target);
                    if (!targets.Contains(target)) RemoveEdge(target, slip.Id);
                }
            foreach (var target in targets)
            {
                if (titleChanged || old is null || !old.Targets.Contains(target)) affected.Add(target);
                if (old is null || !old.Targets.Contains(target))
                    Edges(target).Add(slip.Id);
            }
        }
        foreach (var id in sources.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            foreach (var target in sources[id].Targets)
            {
                affected.Add(target);
                RemoveEdge(target, id);
            }
            sources.Remove(id);
            affected.Add(id);
        }
        foreach (var (target, edges) in edgeChanges)
        {
            if (edges.Count == 0) incoming.Remove(target);
            else incoming[target] = edges.ToImmutable();
        }
        // Reorder only the target lists that actually have incoming edges.
        if (reordered) affected.UnionWith(incoming.Keys);
        foreach (var target in affected)
        {
            if (!sources.ContainsKey(target) || !incoming.TryGetValue(target, out var ids) || ids.Count == 0)
            {
                backlinks.Remove(target);
                continue;
            }
            var links = ids.OrderBy(position).Select(id => new ZetlSlipBacklink(id, sources[id].Title, target)).ToArray();
            if (!backlinks.TryGetValue(target, out var old) || !old.SequenceEqual(links)) backlinks[target] = links;
        }
        return new(sources.ToImmutable(), incoming.ToImmutable(), order, backlinks.ToImmutable(), parsed);

        void RemoveEdge(string target, string source)
        {
            Edges(target).Remove(source);
        }

        ImmutableHashSet<string>.Builder Edges(string target)
        {
            if (!edgeChanges.TryGetValue(target, out var edges))
                edgeChanges[target] = edges = incoming.GetValueOrDefault(target,
                    ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal)).ToBuilder();
            return edges;
        }
    }
}
