using System.Text.Json;
using KASTN;
using Xunit;
using ZETL;
using ZETL.Contracts;

namespace ZETL.Tests;

public class KastnIncrementalSnapshotTests
{
    [Fact]
    public void WireSnapshotsReuseParsingButQueriesReturnCurrentObjects()
    {
        var project = Project();
        var first = new KastnProjectIndex(project);
        var original = first.Backlinks;
        var next = Wire(project with { ChangeSequence = 2, Buckets = [project.Buckets[0] with { Revision = 2, Name = "Renamed" }, project.Buckets[1]] });
        var index = new KastnProjectIndex(next, first);
        Assert.Same(original, index.Backlinks);
        Assert.Equal(0, index.ParsedBacklinkSourceCount);
        Assert.Same(next.Buckets[0], index.Children(null).First());
        Assert.Same(next.Slips[1], index.Slips("child").First());
        Assert.Same(next.Slips[1], index.Slip("source"));
        Assert.Equal("Renamed > child", index.BucketPathLabel(next.Buckets[1]));
        Assert.Equal("root > child", first.BucketPathLabel(project.Buckets[1]));
    }

    [Fact]
    public void TextEditsParseOneSourceAndTitleEditsReuseItsTokens()
    {
        var project = Project();
        var first = new KastnProjectIndex(project);
        var original = first.Backlinks;
        var edit = project with { Slips = project.Slips.Select(s => s.Id == "source" ? s with { Text = s.Text + " tail", Revision = 2 } : s).ToArray() };
        var second = new KastnProjectIndex(Wire(edit), first);
        Assert.Same(original, second.Backlinks); // Same edges and authored source title.
        Assert.Equal(1, second.ParsedBacklinkSourceCount);
        var renamed = edit with { Slips = edit.Slips.Select(s => s.Id == "source" ? s with { Title = "New title", Revision = 3 } : s).ToArray() };
        var third = new KastnProjectIndex(Wire(renamed), second);
        Assert.Equal("New title", third.Backlinks["target"].First().SourceTitle);
        Assert.Equal(0, third.ParsedBacklinkSourceCount);
        Assert.Equal("Source", original["target"].First().SourceTitle);
        var removed = renamed with { Slips = renamed.Slips.Select(s => s.Id == "source" ? s with { Text = "no links" } : s).ToArray() };
        var fourth = new KastnProjectIndex(removed, third);
        Assert.Equal("second", Assert.Single(fourth.Backlinks["target"]).SourceSlipId);
        Assert.Equal(1, fourth.ParsedBacklinkSourceCount);
    }

    [Fact]
    public void AddingAndRemovingTargetsResolvesCachedUnresolvedEdges()
    {
        var project = Project();
        var first = new KastnProjectIndex(project);
        Assert.False(first.Backlinks.ContainsKey("missing"));
        var added = project with { Slips = [..project.Slips, Slip("missing", "root")] };
        var second = new KastnProjectIndex(added, first);
        Assert.Equal("source", Assert.Single(second.Backlinks["missing"]).SourceSlipId);
        Assert.Equal(1, second.ParsedBacklinkSourceCount);
        var removed = added with { Slips = added.Slips.Where(s => s.Id != "target").ToArray() };
        var third = new KastnProjectIndex(removed, second);
        Assert.False(third.Backlinks.ContainsKey("target"));
        Assert.Equal(0, third.ParsedBacklinkSourceCount);
        var restored = new KastnProjectIndex(project, third);
        Assert.Equal(["source", "second"], restored.Backlinks["target"].Select(l => l.SourceSlipId));
        Assert.Equal(1, restored.ParsedBacklinkSourceCount);
        Assert.Equal(2, first.Backlinks["target"].Count);
    }

    [Fact]
    public void ReordersAndMovesKeepCanonicalBacklinkAndBucketOrder()
    {
        var project = Project();
        var first = new KastnProjectIndex(project);
        var original = first.Backlinks;
        var reversed = project with { Slips = project.Slips.Reverse().Select(s => s.Id == "source" ? s with { BucketId = "root" } : s).ToArray() };
        var next = new KastnProjectIndex(Wire(reversed), first);
        Assert.Equal(["second", "source"], next.Backlinks["target"].Select(l => l.SourceSlipId));
        Assert.Equal(0, next.ParsedBacklinkSourceCount);
        Assert.Equal(["second", "source", "target"], next.Slips("root").Select(s => s.Id));
        Assert.Empty(next.Slips("child"));
        Assert.Equal(["source", "second"], original["target"].Select(l => l.SourceSlipId));
    }

    [Fact]
    public void DeferredSnapshotsCompareWithLastParsedStateWithoutKeepingAChain()
    {
        var project = Project();
        var first = new KastnProjectIndex(project);
        var original = first.Backlinks;
        var changed = project with { Slips = project.Slips.Select(s => s.Id == "source" ? s with { Text = "Removed" } : s).ToArray() };
        var hidden = new KastnProjectIndex(changed, first);
        Assert.Equal(0, hidden.ParsedBacklinkSourceCount);
        var latest = changed with { Slips = changed.Slips.Select(s => s.Id == "second" ? s with { Text = "Also removed" } : s).ToArray() };
        var visible = new KastnProjectIndex(latest, hidden);
        Assert.Empty(visible.Backlinks);
        Assert.Equal(2, visible.ParsedBacklinkSourceCount);
        Assert.Equal(2, original["target"].Count);
        var otherProject = new KastnProjectIndex(project with { Id = "other" }, visible);
        Assert.Equal(2, otherProject.Backlinks["target"].Count);
        Assert.Equal(project.Slips.Count, otherProject.ParsedBacklinkSourceCount);
    }

    [Fact]
    public void IncrementalBacklinksMatchFullRebuildAcrossMixedWireSnapshots()
    {
        var random = new Random(7241);
        var project = Project();
        var index = new KastnProjectIndex(project);
        var saved = new List<(KastnProjectIndex Index, IReadOnlyDictionary<string, IReadOnlyList<ZetlSlipBacklink>> Expected)>();
        for (var step = 0; step < 120; step++)
        {
            AssertLinks(ZetlSlipLinks.BuildBacklinkIndex(project), index.Backlinks);
            if (step % 20 == 0) saved.Add((index, ZetlSlipLinks.BuildBacklinkIndex(project)));
            var slips = project.Slips.ToList();
            var pick = random.Next(slips.Count);
            switch (step % 6)
            {
                case 0: slips[pick] = slips[pick] with { Text = $"[[target|Cached]] [[missing|M]] [[added-{step - 2}|A]] [[target|Repeated]] [[broken]]", Revision = step + 2 }; break;
                case 1: slips[pick] = slips[pick] with { Title = step % 3 == 0 ? "" : $"Title {step}" }; break;
                case 2: slips.Add(Slip($"added-{step}", "child") with { Text = "[[source|S]] [[target|T]]" }); break;
                case 3: if (slips.Count > 2) slips.RemoveAt(pick); break;
                case 4: slips.Reverse(); break;
                case 5: slips[pick] = slips[pick] with { BucketId = slips[pick].BucketId == "root" ? "child" : "root", Checked = !slips[pick].Checked }; break;
            }
            project = Wire(project with { ChangeSequence = step + 2, Slips = slips });
            index = new KastnProjectIndex(project, index);
        }
        AssertLinks(ZetlSlipLinks.BuildBacklinkIndex(project), index.Backlinks);
        foreach (var (old, expected) in saved) AssertLinks(expected, old.Backlinks);
    }

    [Fact]
    public void ContentOnlyTreeUpdatesAdoptWireSnapshotsWithoutChangingHierarchy()
    {
        var project = Project();
        var projection = new KastnTreeProjection();
        Assert.True(projection.Update(new(project), false, 24));
        var root = projection.Find("root")!;
        var child = projection.Find("child")!;
        var source = projection.Find("source")!;
        child.IsExpanded = false;
        var changes = 0;
        root.Children.CollectionChanged += (_, _) => changes++;
        child.Children.CollectionChanged += (_, _) => changes++;
        project = Wire(project with
        {
            Buckets = [project.Buckets[0] with { Revision = 2, Name = "Fresh root", RenderKind = "group" }, project.Buckets[1]],
            Slips = project.Slips.Select(s => s.Id == "source" ? s with { Revision = 2, Title = "Fresh source", ExcludedFromViews = true }
                : s with { Revision = 2, InlineStyles = [new() { Start = 0, Length = 1, Kind = "bold" }] }).ToArray()
        });
        Assert.False(projection.Update(new(project), false, 24));
        Assert.Same(source, projection.Find("source"));
        Assert.Same(project.Slips[1], source.Slip);
        Assert.Equal("Fresh source", source.Label);
        Assert.Equal(1, root.HiddenCount);
        Assert.Equal(2, root.IncludedCount);
        Assert.Equal(1, child.HiddenCount);
        Assert.True(root.IsContainerBucket);
        Assert.False(child.IsExpanded);
        Assert.Equal(0, changes);
        foreach (var slip in project.Slips) Assert.Same(slip, projection.Find(slip.Id)!.Slip);
        AssertTree(KastnWorkbench.BuildProjectTree(new(project), project.Slips, false, 24), projection.Roots);
        Assert.False(projection.Update(new(Wire(project)), false, 10));
        Assert.Equal("Fresh sou…", source.Label);
        projection.Clear();
        Assert.True(projection.Update(new(project), false, 24));
    }

    [Theory]
    [InlineData("move")]
    [InlineData("reorder")]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("picture")]
    [InlineData("parent")]
    [InlineData("deleted")]
    [InlineData("mode")]
    public void StructuralTreeChangesUseFullReconciliation(string change)
    {
        var project = Project();
        var projection = new KastnTreeProjection();
        projection.Update(new(project), false, 24);
        project = change switch
        {
            "move" => project with { Slips = project.Slips.Select(s => s.Id == "source" ? s with { BucketId = "root" } : s).ToArray() },
            "reorder" => project with { Slips = project.Slips.Reverse().ToArray() },
            "add" => project with { Slips = [..project.Slips, Slip("new", "child")] },
            "remove" => project with { Slips = project.Slips.Skip(1).ToArray() },
            "picture" => project with { Slips = project.Slips.Select(s => s.Id == "source" ? s with { Type = ZetlSlipType.Picture } : s).ToArray() },
            "parent" => project with { Buckets = [project.Buckets[0], project.Buckets[1] with { ParentBucketId = null }] },
            "deleted" => project with { Buckets = [project.Buckets[0], project.Buckets[1] with { Settings = new() { Kind = "Deleted" } }] },
            _ => project
        };
        project = Wire(project);
        Assert.True(projection.Update(new(project), change == "mode", 24));
        AssertTree(KastnWorkbench.BuildProjectTree(new(project), project.Slips, change == "mode", 24), projection.Roots);
    }

    private static void AssertTree(IReadOnlyList<KastnTreeNode> expected, IReadOnlyList<KastnTreeNode> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var (a, b) = (expected[i], actual[i]);
            Assert.Equal((a.Id, a.Label, a.IsPicture, a.IsStructural, a.IsExcluded, a.IncludedCount, a.HiddenCount, a.IsContainerBucket),
                (b.Id, b.Label, b.IsPicture, b.IsStructural, b.IsExcluded, b.IncludedCount, b.HiddenCount, b.IsContainerBucket));
            AssertTree(a.Children, b.Children);
        }
    }

    private static void AssertLinks(IReadOnlyDictionary<string, IReadOnlyList<ZetlSlipBacklink>> expected,
        IReadOnlyDictionary<string, IReadOnlyList<ZetlSlipBacklink>> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (id, links) in expected) Assert.Equal(links, actual[id]);
    }

    private static ZetlProjectSnapshot Wire(ZetlProjectSnapshot project) =>
        JsonSerializer.Deserialize<ZetlProjectSnapshot>(JsonSerializer.Serialize(project))!;
    private static ZetlProjectSnapshot Project() => new()
    {
        Id = "incremental", Name = "Project", MetadataRevision = 1, ChangeSequence = 1,
        Buckets = [new() { Id = "root", Name = "root", Revision = 1 }, new() { Id = "child", Name = "child", ParentBucketId = "root", Revision = 1 }],
        Slips = [Slip("target", "root"), Slip("source", "child") with { Title = "Source", Text = "[[target|T]] [[target|duplicate]] [[missing|M]] [[broken]]" },
            Slip("second", "root") with { Text = "[[target|T]]" }]
    };
    private static ZetlSlipSnapshot Slip(string id, string bucket) => new()
    {
        Id = id, BucketId = bucket, Revision = 1, Type = ZetlSlipType.Text, Text = id,
        Source = "test", CapturedAtUtc = DateTimeOffset.UnixEpoch
    };
}
