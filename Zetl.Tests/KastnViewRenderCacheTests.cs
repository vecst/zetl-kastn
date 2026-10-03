using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public class KastnViewRenderCacheTests
{
    [Fact]
    public void EquivalentInputsReuseOutputAndClearForcesANewRender()
    {
        var project = Project();
        var view = new ZetlViewDocument { Id = "view", Kind = ZetlViewKinds.Markdown };
        var settings = new ZetlAppSettings();
        var cache = new KastnViewRenderCache();
        var key = KastnViewRenderKey.Create(project, project.Slips, view, settings);
        var output = cache.GetText(key, () => ZetlViewRenderer.Render(project, project.Slips, view));
        Assert.Contains("Project", output);
        var equivalent = KastnViewRenderKey.Create(project with { }, project.Slips.ToArray(), view, settings);
        Assert.Equal(output, cache.GetText(equivalent, () => throw new Exception("Unchanged view was rendered again.")));
        cache.Clear();
        Assert.Equal("refreshed", cache.GetText(equivalent, () => "refreshed"));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("content")]
    [InlineData("metadata")]
    [InlineData("filter")]
    [InlineData("order")]
    [InlineData("view_kind")]
    [InlineData("view_title")]
    [InlineData("view_sections")]
    [InlineData("list_preference")]
    [InlineData("untitled_title")]
    public void ChangesToRenderInputsInvalidateOutput(string change)
    {
        var project = Project();
        IReadOnlyList<ZetlSlipSnapshot> visible = project.Slips;
        var view = new ZetlViewDocument { Id = "same-view", Kind = ZetlViewKinds.Markdown };
        var settings = new ZetlAppSettings();
        var cache = new KastnViewRenderCache();
        var original = KastnViewRenderKey.Create(project, visible, view, settings);
        cache.GetText(original, () => "before");

        switch (change)
        {
            case "project": project = project with { Id = "another" }; break;
            case "content": project = project with { ChangeSequence = 2 }; break;
            case "metadata": project = project with { MetadataRevision = 2 }; break;
            case "filter": visible = [project.Slips[0]]; break;
            case "order": visible = project.Slips.Reverse().ToArray(); break;
            case "view_kind": view.Kind = ZetlViewKinds.Html; break;
            case "view_title": view.Title = "Updated title"; break;
            case "view_sections": view.Sections.Add(new() { Title = "Section", Buckets = ["Bucket"] }); break;
            case "list_preference": settings.KastnPreferSlipKindOverBucketKind = !settings.KastnPreferSlipKindOverBucketKind; break;
            case "untitled_title": settings.UntitledSlipTitle = "New note"; break;
        }

        var updated = KastnViewRenderKey.Create(project, visible, view, settings);
        Assert.NotEqual(original, updated);
        Assert.Equal("after", cache.GetText(updated, () => "after"));
    }

    [Fact]
    public void FailedRenderCanBeRetriedWithoutServingPreviousOutput()
    {
        var project = Project();
        var key = KastnViewRenderKey.Create(project, project.Slips, new(), new());
        var cache = new KastnViewRenderCache();
        cache.GetText(key, () => "old");
        var changed = key with { ChangeSequence = 2 };
        Assert.Throws<InvalidOperationException>(() => cache.GetText(changed, () => throw new InvalidOperationException()));
        Assert.Equal("new", cache.GetText(changed, () => "new"));
    }

    private static ZetlProjectSnapshot Project() => new()
    {
        Id = "project", Name = "Project", MetadataRevision = 1, ChangeSequence = 1,
        Buckets = [new() { Id = "bucket", Name = "Bucket", Revision = 1 }],
        Slips = new[] { "one", "two" }.Select(id => new ZetlSlipSnapshot
        {
            Id = id, Revision = 1, Type = ZetlSlipType.Text, Text = id,
            BucketId = "bucket", Source = "copy", CapturedAtUtc = DateTimeOffset.UnixEpoch
        }).ToArray()
    };
}
