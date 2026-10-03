using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public class KastnSelectionContextTests
{
    [Fact]
    public void ExplicitSelectionUsesCurrentSnapshotsInDocumentOrderAcrossBuckets()
    {
        var first = Slip("first", "a");
        var second = Slip("second", "b");
        var project = Project(first, second);
        var ids = new List<string> { second.Id, "missing", first.Id, second.Id };
        var context = new KastnSelectionContext(new(project), new KastnSelection.Slips(ids), "other", "b");
        ids.Clear();

        Assert.Equal(new[] { "first", "second" }, context.Slips.Select(slip => slip.Id));
        Assert.Same(first, context.Slips[0]);
        Assert.Same(second, context.Slips[1]);
        Assert.Equal(4, Assert.IsType<KastnSelection.Slips>(context.Selection).SlipIds.Count);
        Assert.Null(context.SingleSlip);
        Assert.Null(context.DetailSlip);

        var updated = second with { Revision = 2, Text = "new version" };
        var next = new KastnSelectionContext(new(project with { Slips = [updated, first] }),
            context.Selection, "other", "b");
        Assert.Same(updated, next.Slips[0]);
        Assert.Same(first, next.Slips[1]);
        Assert.Same(second, context.Slips[1]);
    }

    [Fact]
    public void BucketTitleTargetsHeadingWithoutExpandingItsSlips()
    {
        var slip = Slip("edited", "a");
        var index = new KastnProjectIndex(Project(slip));
        var context = new KastnSelectionContext(index, new KastnSelection.BucketTitle("a"), slip.Id, "a");

        Assert.Empty(context.Slips);
        Assert.Same(index.Bucket("a"), context.TitleBucket);
        Assert.Same(slip, context.DetailSlip);
        var enabled = Availability(context);
        Assert.True(enabled.FormatBucketEnabled);
        Assert.True(enabled.AlignEnabled);
        Assert.False(enabled.MoveOrDeleteEnabled);

        var deleted = new KastnSelectionContext(index, new KastnSelection.BucketTitle("deleted"), null, "deleted");
        Assert.Null(deleted.TitleBucket);
        Assert.False(Availability(deleted).FormatBucketEnabled);
        Assert.False(Availability(deleted).CreateSlipEnabled);
    }

    [Fact]
    public void EditorFallbackOnlySuppliesSlipTargetsWhenNoExplicitSelectionExists()
    {
        var slip = Slip("edited", "a");
        var index = new KastnProjectIndex(Project(slip));
        var fallback = new KastnSelectionContext(index, new KastnSelection.None(), slip.Id, null);
        Assert.Same(slip, Assert.Single(fallback.Slips));
        Assert.True(Availability(fallback).EditorEnabled);

        var missingSelection = new KastnSelectionContext(index, new KastnSelection.Slips(["missing"]), slip.Id, null);
        Assert.Empty(missingSelection.Slips);
        Assert.Same(slip, missingSelection.DetailSlip);
        Assert.False(Availability(missingSelection).MoveOrDeleteEnabled);

        var noProject = new KastnSelectionContext(null, fallback.Selection, null, null);
        Assert.Empty(noProject.Slips);
        Assert.Null(noProject.DetailSlip);
        Assert.False(Availability(noProject).CreateSlipEnabled);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void OfflineBusyOrConflictedEditorCannotSaveFormatOrDelete(bool online, bool busy, bool conflict)
    {
        var slip = Slip("edited", "a");
        var context = new KastnSelectionContext(new(Project(slip)), new KastnSelection.Slips([slip.Id]), slip.Id, "a");
        var enabled = KastnCommandAvailability.Compute(context, online, busy, conflict, addingSlip: false);

        Assert.False(enabled.EditorEnabled);
        Assert.False(enabled.FormatSelectionEnabled);
        Assert.False(enabled.AlignEnabled);
        Assert.False(enabled.SlipPropertiesEnabled);
        Assert.False(enabled.MoveOrDeleteEnabled);
    }

    [Fact]
    public void StructuralSlipCanBeMovedButHasNoEditorOrContentFormatting()
    {
        var slip = Slip("divider", "a") with { BlockKind = "divider" };
        var context = new KastnSelectionContext(new(Project(slip)), new KastnSelection.Slips([slip.Id]), slip.Id, "a");
        var enabled = Availability(context);
        Assert.False(enabled.EditorEnabled);
        Assert.False(enabled.FormatSelectionEnabled);
        Assert.False(enabled.AlignEnabled);
        Assert.False(enabled.SlipPropertiesEnabled);
        Assert.True(enabled.MoveOrDeleteEnabled);
    }

    [Fact]
    public void DeletedSlipCanBeRestoredButCannotBeMovedDeletedOrAligned()
    {
        var slip = Slip("deleted-slip", "deleted");
        var context = new KastnSelectionContext(new(Project(slip)), new KastnSelection.Slips([slip.Id]), slip.Id, "deleted");
        var enabled = Availability(context);
        Assert.True(context.IsDeleted(slip));
        Assert.True(enabled.RestoreSlipEnabled);
        Assert.False(enabled.MoveOrDeleteEnabled);
        Assert.False(enabled.AlignEnabled);
        Assert.False(enabled.SlipPropertiesEnabled);
        Assert.False(enabled.CreateSlipEnabled);
    }

    [Fact]
    public void BatchFormatsEligibleTextWithoutBindingASingleEditor()
    {
        var text = Slip("text", "a");
        var picture = Slip("picture", "b") with { Type = ZetlSlipType.Picture };
        var project = Project(text, picture);
        var context = new KastnSelectionContext(new(project), new KastnSelection.Slips([picture.Id, text.Id]), null, "b");
        var enabled = Availability(context);
        Assert.False(enabled.EditorEnabled);
        Assert.False(enabled.SlipPropertiesEnabled);
        Assert.True(enabled.FormatSelectionEnabled);
        Assert.True(enabled.AlignEnabled);
        Assert.True(enabled.MoveOrDeleteEnabled);

        var onlyPictures = new KastnSelectionContext(new(Project(picture, picture with { Id = "other-picture" })),
            new KastnSelection.Slips([picture.Id, "other-picture"]), null, "b");
        Assert.False(Availability(onlyPictures).FormatSelectionEnabled);
        Assert.False(Availability(onlyPictures).AlignEnabled);
    }

    private static KastnCommandAvailability Availability(KastnSelectionContext context) =>
        KastnCommandAvailability.Compute(context, online: true, busy: false, hasConflict: false, addingSlip: false);

    private static ZetlSlipSnapshot Slip(string id, string bucketId) => new()
    {
        Id = id, BucketId = bucketId, Revision = 1, Text = id, Source = "copy",
        Type = ZetlSlipType.Text, CapturedAtUtc = DateTimeOffset.UtcNow
    };

    private static ZetlProjectSnapshot Project(params ZetlSlipSnapshot[] slips) => new()
    {
        Id = "project", Name = "Project", MetadataRevision = 1, ChangeSequence = 1,
        Buckets =
        [
            new() { Id = "a", Name = "First", Revision = 1 },
            new() { Id = "b", Name = "Second", Revision = 1 },
            new() { Id = "deleted", Name = "Deleted", Revision = 1, Settings = new() { Kind = "Deleted" } }
        ],
        Slips = slips
    };
}
