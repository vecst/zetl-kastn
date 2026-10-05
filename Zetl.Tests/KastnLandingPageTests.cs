using System.Collections.Specialized;
using System.Text.Json;
using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public class KastnLandingPageTests
{
    [Fact]
    public void LanesResolveUnderlyingProjectsAndReplayOverlaysWithoutDuplicatingTheLibrary()
    {
        var h = new Harness();
        h.Page.UpdateProjects([
            Project("main") with { UnderlyingLane = ZetlStateRules.NormalLane },
            Project("queue") with { Kind = ZetlStateRules.TemporaryConsumableProjectKind, ActiveLane = ZetlStateRules.NormalLane, VisibleSlipCount = 1 },
            Project("shift") with { ActiveLane = ZetlStateRules.ShiftLane },
            Project("library"), Project("archived") with { Status = "Archived" }
        ]);
        var main = h.Page.Lanes[0];
        Assert.Equal("main", main.Project?.Id);
        Assert.Equal("queue", main.OverlayProject?.Id);
        Assert.Equal("1 replay item left", main.OverlayProgress);
        Assert.Equal("shift", h.Page.Lanes[1].Project?.Id);
        Assert.Equal("library", Assert.Single(h.Page.Projects).Id);
        Assert.DoesNotContain(h.Page.RecentProjects, card => card.IsTemporary || card.IsArchived);
        Assert.Contains(h.Page.RecentProjects, card => card.Id == "main");
        h.Page.SetArchived(true);
        Assert.Equal("archived", Assert.Single(h.Page.Projects).Id);
        Assert.Same(main, h.Page.Lanes[0]);
    }

    [Fact]
    public void RecentsAreBoundedAndSortedByActivityThenNameWhileFinishedProjectsRemainCurrent()
    {
        var h = new Harness();
        var summaries = Enumerable.Range(0, 15).Select(i => Project($"p{i:00}") with
        { LastActivityUtc = DateTimeOffset.UnixEpoch.AddDays(i), Status = i == 14 ? "Finished" : "Active" }).ToArray();
        h.Page.UpdateProjects([.. summaries, Project("a") with { LastActivityUtc = summaries[14].LastActivityUtc }]);
        Assert.Equal(10, h.Page.RecentProjects.Count);
        Assert.Equal("a", h.Page.RecentProjects[0].Id);
        Assert.Equal("p14", h.Page.RecentProjects[1].Id);
        Assert.Equal("Reactivate", h.Page.RecentProjects[1].StatusActionLabel);
        Assert.Equal(16, h.Page.Projects.Count);
    }

    [Fact]
    public void WireEquivalentSnapshotsDoNotResetCardsAndChangedCardsCarryFreshRevisions()
    {
        var h = new Harness();
        var summaries = new[] { Project("a"), Project("b"), Project("c") };
        h.Page.UpdateProjects(summaries);
        var first = h.Page.Projects[0];
        var originalB = h.Page.Projects[1];
        var events = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)h.Page.Projects).CollectionChanged += (_, args) => events.Add(args.Action);
        h.Page.UpdateProjects(JsonSerializer.Deserialize<ZetlProjectSummary[]>(JsonSerializer.Serialize(summaries))!);
        Assert.Empty(events);
        Assert.Same(first, h.Page.Projects[0]);
        Assert.Same(first, h.Page.RecentProjects[0]);
        h.Page.UpdateProjects([summaries[0], summaries[1] with { MetadataRevision = 7, PreviewText = "fresh preview" }, summaries[2]]);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Replace }, events);
        Assert.NotSame(originalB, h.Page.Projects[1]);
        Assert.Equal(7, h.Page.Projects[1].MetadataRevision);
        Assert.Equal("fresh preview", h.Page.Projects[1].PreviewText);
        Assert.Equal(1, originalB.MetadataRevision);
        Assert.Same(first, h.Page.Projects[0]);
    }

    [Fact]
    public void LibraryReordersRemovesAndAddsWithoutResetAndUsesAllNamesForCreationValidation()
    {
        var h = new Harness();
        var a = Project("a");
        var b = Project("b");
        h.Page.UpdateProjects([a, b, Project("hidden") with { Status = "Archived", Name = "Reserved" },
            Project("pinned") with { ActiveLane = ZetlStateRules.NormalLane },
            Project("temporary") with { Kind = ZetlStateRules.TemporaryConsumableProjectKind, Name = "Replay" }]);
        var oldB = h.Page.Projects[1];
        var events = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)h.Page.Projects).CollectionChanged += (_, args) => events.Add(args.Action);
        Assert.True(h.Page.ProjectNameExists(" reserved "));
        Assert.True(h.Page.ProjectNameExists("PINNED"));
        Assert.True(h.Page.ProjectNameExists("Replay"));
        h.Page.UpdateProjects([b, Project("d"), a with { Name = "z" }]);
        Assert.Equal(new[] { "b", "d", "a" }, h.Page.Projects.Select(card => card.Id));
        Assert.Same(oldB, h.Page.Projects[0]);
        h.Page.UpdateProjects([b]);
        Assert.Single(h.Page.Projects);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, events);
        Assert.False(h.Page.ProjectNameExists("Reserved"));
    }

    [Fact]
    public void TemplateVisitsReloadNestedSourceChangesAndPreserveUnchangedCards()
    {
        var h = new Harness();
        var capture = KastnProjectCreationTests.Template();
        capture.Id = "capture";
        capture.Type = ZetlTemplateTypes.Capture;
        var consumable = ZetlTemplateDefaults.Clone(capture);
        consumable.Id = "consumable";
        consumable.Type = ZetlTemplateTypes.Consumable;
        h.Templates = [capture, consumable];
        h.Page.Visit(KastnLandingSection.Templates);
        var first = Assert.Single(h.Page.Templates);
        h.Templates = [ZetlTemplateDefaults.Clone(capture), consumable];
        h.Page.Visit(KastnLandingSection.Templates);
        Assert.Same(first, Assert.Single(h.Page.Templates));
        h.Templates[0].Buckets[0].Cards.Add(new() { Text = "new seed" });
        h.Page.Visit(KastnLandingSection.Templates);
        Assert.NotSame(first, Assert.Single(h.Page.Templates));
        Assert.DoesNotContain(first.Source.Buckets[0].Cards, card => card.Text == "new seed");
        h.Page.SetTemplateType(true);
        Assert.Equal("consumable", Assert.Single(h.Page.Templates).Source.Id);
        h.Page.Visit(KastnLandingSection.Projects);
        h.Page.Visit(KastnLandingSection.Templates);
        Assert.True(h.Page.ShowingConsumable);
        h.Templates = [];
        h.Page.Visit(KastnLandingSection.Templates);
        Assert.Empty(h.Page.Templates);
    }

    [Fact]
    public void CreationVisitsReloadViewAndTemplateNamesAndUseIdsForMissingDependencies()
    {
        var h = new Harness();
        h.Templates = [KastnProjectCreationTests.Template()];
        h.Views = [new() { Id = "custom-view", Name = "Pretty" }];
        h.Creations = [new() { Id = "starter", Name = "Starter", TemplateId = h.Templates[0].Id, ViewIds = ["custom-view"] }];
        h.Page.Visit(KastnLandingSection.Creations);
        var first = Assert.Single(h.Page.Creations);
        Assert.Contains("View: Pretty", first.Detail);
        h.Page.Visit(KastnLandingSection.Creations);
        Assert.Same(first, Assert.Single(h.Page.Creations));
        h.Views = [new() { Id = "custom-view", Name = "Renamed" }];
        h.Page.Visit(KastnLandingSection.Creations);
        Assert.Contains("View: Renamed", Assert.Single(h.Page.Creations).Detail);
        h.Templates = [];
        h.Views = [];
        h.Page.Visit(KastnLandingSection.Creations);
        Assert.Equal($"Template: {h.Creations[0].TemplateId}  ·  View: custom-view", Assert.Single(h.Page.Creations).Detail);
        h.Creations[0].ViewIds = [];
        h.Page.Visit(KastnLandingSection.Creations);
        Assert.EndsWith("View: no view", Assert.Single(h.Page.Creations).Detail);
    }

    [Theory]
    [InlineData("Projects", "Projects")]
    [InlineData("Templates", "Templates")]
    [InlineData("Creations", "Create")]
    public void LandingPresentationPreservesChoicesForEmptyLibrariesAndDisablesOfflineActions(string section, string title)
    {
        var h = new Harness();
        h.Page.Visit(Enum.Parse<KastnLandingSection>(section));
        var online = h.Page.Presentation(true, true);
        Assert.True(online.Choices);
        Assert.Equal(title, online.Title);
        Assert.False(online.HasProjects);
        Assert.True(online.Online);
        Assert.False(h.Page.Presentation(true, false).Online);
        Assert.False(h.Page.Presentation(false, true).Choices);
    }

    [Fact]
    public void RetiredLandingCannotReloadOrConsumeLaterActionSuppression()
    {
        var h = new Harness();
        var old = h.Page.BeginCardAction();
        var current = h.Page.BeginCardAction();
        h.Page.EndCardAction(old);
        Assert.True(h.Page.ConsumeSuppressedSelection());
        Assert.False(h.Page.ConsumeSuppressedSelection());
        h.Page.BeginCardAction();
        h.Page.EndCardAction(current);
        Assert.True(h.Page.ConsumeSuppressedSelection());
        h.Page.Retire();
        h.Page.Visit(KastnLandingSection.Templates);
        h.Page.SetTemplateType(true);
        h.Page.ReloadCreations();
        h.Page.UpdateProjects([Project("late")]);
        h.Page.BeginCardAction();
        Assert.False(h.Page.ConsumeSuppressedSelection());
        Assert.Equal(0, h.TemplateLoads);
        Assert.Empty(h.Page.Projects);
        Assert.False(h.Page.Presentation(true, true).Choices);
    }

    [Fact]
    public void NativeCardsRejectOldScopesEvenWhenProjectIdsAndRevisionsAreReused()
    {
        var h = new Harness();
        h.Page.UpdateProjects([Project("same")]);
        var old = Assert.Single(h.Page.Projects);
        Assert.True(h.Page.OwnsProjectCard(old));
        h.Page.ResetProjectActions();
        Assert.False(h.Page.OwnsProjectCard(old));
        h.Page.UpdateProjects([Project("same")]);
        var current = Assert.Single(h.Page.Projects);
        Assert.NotSame(old, current);
        Assert.True(h.Page.OwnsProjectCard(current));
        h.Page.UpdateProjects([Project("same")]);
        Assert.Same(current, Assert.Single(h.Page.Projects));
        h.Page.Retire();
        Assert.False(h.Page.OwnsProjectCard(current));
    }

    internal static ZetlProjectSummary Project(string id) => new()
    { Id = id, Name = id, MetadataRevision = 1, ChangeSequence = 1 };

    private sealed class Harness
    {
        public ZetlTemplateDocument[] Templates = [];
        public ZetlCreationTypeDocument[] Creations = [];
        public ZetlViewDocument[] Views = [];
        public int TemplateLoads;
        public readonly KastnLandingPage Page;
        public Harness() => Page = new(new(() => { TemplateLoads++; return Templates; }, () => Creations, () => Views),
            lane => lane == ZetlStateRules.NormalLane ? "Main" : "Shift");
    }
}
