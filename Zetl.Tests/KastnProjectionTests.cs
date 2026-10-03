using System.Collections;
using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public class KastnProjectionTests
{
    [Fact]
    public void IndexedQueriesPreserveOrderWithoutRescanningSnapshotBuckets()
    {
        var buckets = new CountingList<ZetlBucketSnapshot>(
        [
            Bucket("child", "root"), Bucket("root"), Bucket("second"),
            Bucket("deleted") with { Settings = new() { Kind = "Deleted" } }
        ]);
        var slips = new[] { Slip("two", "child"), Slip("one", "root"), Slip("three", "child") };
        var project = Project(buckets, slips);
        var index = new KastnProjectIndex(project);
        buckets.Enumerations = 0;

        var ordered = index.OrderedBuckets();
        Assert.Equal(new[] { "root", "child", "second" }, ordered.Select(bucket => bucket.Id));
        Assert.Equal(new[] { "root", "child", "second" }, ordered.Select(bucket => bucket.Id));
        Assert.Equal(new[] { "deleted" }, index.OrderedBuckets(deletedOnly: true).Select(bucket => bucket.Id));
        Assert.Equal(new[] { "two", "three" }, index.Slips("child").Select(slip => slip.Id));
        Assert.Equal("root > child", index.BucketPathLabel(index.Bucket("child")!));
        Assert.True(index.IsDescendant("child", "root"));
        Assert.False(index.IsDescendant("root", "child"));
        Assert.True(index.DescendantBucketIds("root").SetEquals(new[] { "root", "child" }));
        Assert.Same(slips[0], index.Slip("two"));
        Assert.Null(index.Bucket("missing"));
        Assert.Empty(index.Slips("missing"));

        var tree = KastnWorkbench.BuildProjectTree(index, project.Slips, deletedOnly: false, maxSlipLabelLength: 24);
        Assert.Equal(new[] { "root", "second" }, tree.Select(node => node.Id));
        Assert.Equal(new[] { "child", "one" }, tree[0].Children.Select(node => node.Id));
        Assert.Equal(0, buckets.Enumerations);
    }

    [Fact]
    public void DescendantAndPathQueriesTerminateOnParentCycles()
    {
        var index = new KastnProjectIndex(Project([Bucket("a", "b"), Bucket("b", "a")], []));
        Assert.True(index.DescendantBucketIds("a").SetEquals(new[] { "a", "b" }));
        Assert.Equal("b > a", index.BucketPathLabel(index.Bucket("a")!));
        Assert.False(index.IsDescendant("a", "missing"));
    }

    [Fact]
    public void TreeReconciliationKeepsLiveNodesWhileUpdatingOrderAndRevisions()
    {
        var first = Project([Bucket("a"), Bucket("b")], [Slip("one", "a"), Slip("two", "a")]);
        var projection = new KastnTreeProjection();
        projection.Update(new(first), deletedOnly: false, maxSlipLabelLength: 24);
        var roots = projection.Roots;
        var a = projection.Find("a");
        var b = projection.Find("b");
        var two = projection.Find("two");

        var revisedSlip = Slip("two", "a") with { Revision = 2, Text = "edited", ExcludedFromViews = true };
        var next = first with
        {
            ChangeSequence = 2,
            Buckets = [Bucket("b"), Bucket("a") with { Name = "renamed", Revision = 2 }],
            Slips = [revisedSlip, Slip("new", "a")]
        };
        projection.Update(new(next), deletedOnly: false, maxSlipLabelLength: 24);

        Assert.Same(roots, projection.Roots);
        Assert.Same(b, roots[0]);
        Assert.Same(a, roots[1]);
        Assert.Equal("renamed", a!.Label);
        Assert.Same(two, projection.Find("two"));
        Assert.Same(revisedSlip, two!.Slip);
        Assert.True(two.IsExcluded);
        Assert.Equal(1, a.IncludedCount);
        Assert.Equal(1, a.HiddenCount);
        Assert.Null(projection.Find("one"));
        Assert.Equal(new[] { "two", "new" }, a.Children.Select(node => node.Id));
        Assert.Same(two, projection.FirstSlip);

        projection.Clear();
        Assert.Empty(roots);
        Assert.Null(projection.Find("two"));
        Assert.Null(projection.FirstSlip);
    }

    [Fact]
    public void TreeProjectionReplacesNodesWhenTheirRepresentationChanges()
    {
        var project = Project([Bucket("a")], [Slip("one", "a")]);
        var projection = new KastnTreeProjection();
        projection.Update(new(project), false, 24);
        var original = projection.Find("one");
        projection.Update(new(project with
        {
            ChangeSequence = 2,
            Slips = [project.Slips[0] with { Type = ZetlSlipType.Picture, Revision = 2 }]
        }), false, 24);
        Assert.NotSame(original, projection.Find("one"));
        Assert.True(projection.Find("one")!.IsPicture);
    }

    [Fact]
    public void LabelLengthIsAnInputRatherThanSharedMutableState()
    {
        var project = Project([Bucket("a")], [Slip("one", "a") with { Text = new string('x', 50) }]);
        var index = new KastnProjectIndex(project);
        var shortTree = KastnWorkbench.BuildProjectTree(index, project.Slips, false, 10);
        var longTree = KastnWorkbench.BuildProjectTree(index, project.Slips, false, 30);
        Assert.Equal(10, shortTree[0].Children[0].Label.Length);
        Assert.Equal(30, longTree[0].Children[0].Label.Length);
        Assert.EndsWith("…", shortTree[0].Children[0].Label);
    }

    [Fact]
    public void BacklinksAreReusedWithinASnapshotAndRecomputedAfterAnEdit()
    {
        var project = Project([Bucket("a")],
        [
            Slip("target", "a"), Slip("source", "a") with { Text = ZetlSlipLinks.Format("target", "Target") }
        ]);
        var index = new KastnProjectIndex(project);
        var backlinks = index.Backlinks;
        Assert.Equal("source", backlinks["target"].Single().SourceSlipId);
        KastnSlipInspector.Build(index, project.Slips[0]);
        Assert.Same(backlinks, index.Backlinks);

        var edited = new KastnProjectIndex(project with
        {
            ChangeSequence = 2,
            Slips = [project.Slips[0], project.Slips[1] with { Revision = 2, Text = "link removed" }]
        });
        Assert.Empty(edited.Backlinks);
        Assert.Single(index.Backlinks["target"]);
    }

    private static ZetlProjectSnapshot Project(IReadOnlyList<ZetlBucketSnapshot> buckets, IReadOnlyList<ZetlSlipSnapshot> slips) => new()
    {
        Id = "project", Name = "Project", ChangeSequence = 1, MetadataRevision = 1,
        Buckets = buckets, Slips = slips
    };

    private static ZetlBucketSnapshot Bucket(string id, string? parent = null) => new()
    {
        Id = id, Name = id, Revision = 1, ParentBucketId = parent
    };

    private static ZetlSlipSnapshot Slip(string id, string bucket) => new()
    {
        Id = id, Revision = 1, Type = ZetlSlipType.Text, BucketId = bucket,
        Text = id, Source = "copy", CapturedAtUtc = DateTimeOffset.UnixEpoch
    };

    private sealed class CountingList<T>(IReadOnlyList<T> items) : IReadOnlyList<T>
    {
        public int Enumerations { get; set; }
        public int Count => items.Count;
        public T this[int index] => items[index];
        public IEnumerator<T> GetEnumerator()
        {
            Enumerations++;
            return items.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
