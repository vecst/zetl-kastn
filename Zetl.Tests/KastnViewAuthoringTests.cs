using System.Text.Json;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public class KastnViewAuthoringTests
{
    [Fact]
    public void CatalogShadowsGlobalIdsAndRetainsIdentityUntilMetadataChanges()
    {
        using var h = new ViewHarness();
        var global = h.View();
        h.Views.Save(global);
        var local = h.View();
        local.Name = "Project copy";
        local.Sections = [new() { Title = "Local", Buckets = ["Inbox"] }];
        h.Store.SaveProjectView(h.Project, local);
        var project = h.Snapshot;
        var catalog = new KastnViewCatalog(h.Views);
        Assert.True(catalog.Refresh(project));
        Assert.Equal("Project copy", catalog.Find(global.Id)!.Name);
        Assert.Equal(global.Name, catalog.Global.Single(view => view.Id == global.Id).Name);
        Assert.True(catalog.IsProjectScoped(global.Id));
        var prior = catalog.Views;
        Assert.False(catalog.Refresh(project with { ChangeSequence = project.ChangeSequence + 1 }));
        Assert.Same(prior, catalog.Views);
        Assert.Equal(global.Id, catalog.SelectDefault(project with { DefaultViewId = global.Id }, "plain").Id);
        Assert.Equal("plain", catalog.SelectDefault(project with { DefaultViewId = "missing" }, "plain").Id);
        Assert.Equal("formatted", catalog.SelectDefault(project, "missing").Id);
        Assert.True(catalog.Refresh(null));
        Assert.False(catalog.IsProjectScoped(global.Id));
        Assert.Equal(global.Name, catalog.Find(global.Id)!.Name);
        Assert.Equal("formatted", catalog.Select("missing").Id);
    }

    [Fact]
    public void DraftCapturesIndependentSectionsAndPreservesUnknownGlobalFields()
    {
        using var h = new ViewHarness();
        var view = h.View(structured: true);
        view.ExtensionData = new() { ["future"] = JsonSerializer.SerializeToElement("global") };
        view.Sections[0].ExtensionData = new() { ["futureSection"] = JsonSerializer.SerializeToElement(7) };
        var draft = new KastnViewEditorDraft(view, false, new(h.Snapshot, 1));
        draft.SetBaseline();
        var captured = draft.Capture();
        draft.Sections[0].Title = "Changed";
        draft.Sections[0].Buckets.Add("Next");
        Assert.True(draft.IsDirty);
        Assert.Equal("Inbox", captured.Sections[0].Title);
        Assert.Single(captured.Sections[0].Buckets);
        Assert.Equal("Inbox", view.Sections[0].Title);
        Assert.Equal(7, draft.Capture().Sections[0].ExtensionData!["futureSection"].GetInt32());
        Assert.Equal("global", draft.Capture().ExtensionData!["future"].GetString());
        draft.AcceptSaved(captured);
        Assert.True(draft.IsDirty);
        draft.SetStructureMode(false, ["Inbox", "Next"]);
        Assert.Empty(draft.Capture().Sections);
        draft.SetStructureMode(true, ["Inbox", "Next"]);
        Assert.Equal("Changed", draft.Capture().Sections[0].Title);
        var second = draft.AddSection(["Inbox", "Next", "Third"]);
        Assert.Equal("Third", second.Title);
        Assert.True(draft.ReorderSection(second, draft.Sections[0], before: true));
        Assert.Same(second, draft.Sections[0]);
        draft.MoveSection(second, 1);
        Assert.Same(second, draft.Sections[1]);
        Assert.False(draft.ReorderSection(draft.Sections[0], second, before: true));
        Assert.False(draft.ReorderSection(second, second, true));
        foreach (var section in draft.Sections.ToArray()) draft.RemoveSection(section, ["Inbox"]);
        Assert.Single(draft.Sections);
        Assert.Equal("Inbox", draft.Sections[0].Title);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScopeMigrationUsesConfirmedOriginalProjectWrites(bool toProject)
    {
        using var h = new ViewHarness();
        var view = h.View(structured: !toProject);
        if (toProject) h.Views.Save(view);
        else h.Store.SaveProjectView(h.Project, view);
        var original = h.Snapshot;
        view.Sections = toProject ? [new() { Title = "Inbox", Buckets = ["Inbox"] }] : [];
        var commands = new List<ZetlCommandEnvelope>();
        var result = await h.Persistence.SaveAsync(view, !toProject, original, command =>
        {
            commands.Add(command);
            return Task.FromResult(h.Service.Execute(command));
        }, () => true);
        Assert.True(result.Success, result.Error);
        var sent = Assert.Single(commands);
        Assert.Equal(original.Id, sent.ProjectId);
        Assert.Equal(original.MetadataRevision, sent.ExpectedTargetRevision);
        Assert.Equal(toProject ? ZetlCommandKind.SaveProjectView : ZetlCommandKind.DeleteProjectView, sent.Kind);
        Assert.Equal(toProject, h.Project.Views.Any(item => item.Id == view.Id));
        Assert.Equal(!toProject, h.Views.LoadAll().Any(item => item.Id == view.Id));
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("command")]
    [InlineData("project")]
    [InlineData("revision")]
    [InlineData("view")]
    [InlineData("malformed")]
    public async Task UnconfirmedProjectWritesNeverRemoveTheGlobalCopy(string failure)
    {
        using var h = new ViewHarness();
        var view = h.View();
        h.Views.Save(view);
        view.Sections = [new() { Title = "Inbox", Buckets = ["Inbox"] }];
        var result = await h.Persistence.SaveAsync(view, false, h.Snapshot, command =>
        {
            var saved = h.Snapshot with { MetadataRevision = h.Snapshot.MetadataRevision + 1, Views = [ZetlProjectSnapshotMapper.ToSnapshot(view)] };
            var response = new ZetlResponseEnvelope
            {
                CommandId = failure == "command" ? "other" : command.CommandId,
                ProjectId = failure == "project" ? "other" : command.ProjectId,
                Status = failure == "conflict" ? ZetlResponseStatus.Conflict : ZetlResponseStatus.Success,
                Payload = JsonSerializer.SerializeToElement(failure == "revision" ? saved with { MetadataRevision = 40 }
                    : failure == "view" ? saved with { Views = [] } : saved, ZetlProtocolJson.Options)
            };
            if (failure == "malformed") response = response with { Payload = JsonSerializer.SerializeToElement("not a project") };
            return Task.FromResult(response);
        }, () => true);
        Assert.False(result.Success);
        Assert.Contains(h.Views.LoadAll(), item => item.Id == view.Id);
        Assert.False(h.Persistence.IsBusy);
    }

    [Fact]
    public async Task DelayedSaveCapturesPayloadRejectsOverlapsAndRetainsAChangedGlobalFile()
    {
        using var h = new ViewHarness();
        var view = h.View();
        h.Views.Save(view);
        view.Sections = [new() { Title = "Inbox", Buckets = ["Inbox"] }];
        var response = new TaskCompletionSource<ZetlResponseEnvelope>();
        ZetlCommandEnvelope? sent = null;
        var saving = h.Persistence.SaveAsync(view, false, h.Snapshot, command => { sent = command; return response.Task; }, () => true);
        Assert.True(h.Persistence.IsBusy);
        view.Name = "Later writing";
        view.Sections[0].Title = "Later heading";
        Assert.False((await h.Persistence.SaveAsync(view, false, h.Snapshot,
            _ => throw new InvalidOperationException("Must not submit another write."), () => true)).Success);
        var changedGlobal = h.View();
        changedGlobal.Description = "Changed in another window";
        h.Views.Save(changedGlobal);
        response.SetResult(h.Service.Execute(sent!));
        Assert.True((await saving).Success);
        Assert.Equal("Authoring view", h.Project.Views.Single().Name);
        Assert.Equal("Inbox", h.Project.Views.Single().Sections.Single().Title);
        Assert.Equal(changedGlobal.Description, h.Views.LoadAll().Single(item => item.Id == view.Id).Description);
        Assert.False(h.Persistence.IsBusy);
    }

    [Fact]
    public async Task FailedScopeRemovalKeepsBothDurableCopiesAndDoesNotReportSuccess()
    {
        using var h = new ViewHarness();
        var view = h.View(structured: true);
        h.Store.SaveProjectView(h.Project, view);
        var original = h.Snapshot;
        h.Project.MetadataRevision++;
        view.Sections.Clear();
        var result = await h.Persistence.SaveAsync(view, true, original, command => Task.FromResult(h.Service.Execute(command)), () => true);
        Assert.False(result.Success);
        Assert.Contains("global copy", result.Error);
        Assert.Contains(h.Project.Views, item => item.Id == view.Id);
        Assert.Contains(h.Views.LoadAll(), item => item.Id == view.Id);
    }

    [Fact]
    public async Task InvalidOrRetiredDraftsAndProtectedViewsNeverWrite()
    {
        using var h = new ViewHarness();
        Task<ZetlResponseEnvelope> NoWrite(ZetlCommandEnvelope _) => throw new InvalidOperationException("Must not submit.");
        Assert.False((await h.Persistence.SaveAsync(h.View(true), false, null, NoWrite, () => true)).Success);
        Assert.False((await h.Persistence.SaveAsync(h.View(), false, h.Snapshot, NoWrite, () => false)).Success);
        var missing = h.View(true);
        Assert.False((await h.Persistence.SaveAsync(missing, true, h.Snapshot, NoWrite, () => true)).Success);
        var invalid = h.View(); invalid.Name = "";
        Assert.False((await h.Persistence.SaveAsync(invalid, false, h.Snapshot, NoWrite, () => true)).Success);
        Assert.False((await h.Persistence.SaveAsync(ZetlViewDefaults.CreateAll()[0], false, h.Snapshot, NoWrite, () => true)).Success);
        Assert.False((await h.Persistence.DeleteAsync(ZetlViewDefaults.CreateAll()[0], false, h.Snapshot, NoWrite, () => true)).Success);
        Assert.DoesNotContain(h.Views.LoadAll(), view => view.Id == missing.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteHonorsCapturedScopeAndRejectsNewerGlobalContents(bool projectScoped)
    {
        using var h = new ViewHarness();
        var view = h.View(projectScoped);
        if (projectScoped) h.Store.SaveProjectView(h.Project, view);
        else h.Views.Save(view);
        var context = h.Snapshot;
        if (!projectScoped)
        {
            var replacement = h.View(); replacement.Name = "Newer"; h.Views.Save(replacement);
            Assert.False((await h.Persistence.DeleteAsync(view, false, context, _ => throw new Exception(), () => true)).Success);
            Assert.Equal("Newer", h.Views.LoadAll().Single(item => item.Id == view.Id).Name);
            view = replacement;
        }
        Assert.True((await h.Persistence.DeleteAsync(view, projectScoped, context, command => Task.FromResult(h.Service.Execute(command)), () => true)).Success);
        Assert.DoesNotContain(h.Project.Views, item => item.Id == view.Id);
        Assert.DoesNotContain(h.Views.LoadAll(), item => item.Id == view.Id);
    }

    private sealed class ViewHarness : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "KastnViews", Guid.NewGuid().ToString("N"));
        public ViewHarness()
        {
            Directory.CreateDirectory(directory);
            Store = new(Path.Combine(directory, "state.json"), "kastn-tests");
            Project = Store.CreateProject("Views", ["Inbox", "Next"], "Inbox");
            Service = new(Store);
            Views = new(Path.Combine(directory, "views"));
            Persistence = new(Views);
        }
        public ZetlStateStore Store { get; }
        public ZetlProject Project { get; }
        public ZetlProjectService Service { get; }
        public ZetlViewStore Views { get; }
        public KastnViewPersistence Persistence { get; }
        public ZetlProjectSnapshot Snapshot => ZetlProjectSnapshotMapper.ToSnapshot(Project);
        public ZetlViewDocument View(bool structured = false) => new()
        {
            Id = "authoring", Name = "Authoring view", Kind = ZetlViewKinds.Html,
            Sections = structured ? [new() { Title = "Inbox", Buckets = ["Inbox"] }] : []
        };
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
