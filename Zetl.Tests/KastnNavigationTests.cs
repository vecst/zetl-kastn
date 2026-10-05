using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public class KastnNavigationTests
{
    private static readonly ZetlSlipSnapshot Old = new() { Id = "old", BucketId = "a", Text = "Original", Revision = 1,
        Type = ZetlSlipType.Text, Source = "kastn", CapturedAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z") };
    private static readonly ZetlSlipSnapshot Next = Old with { Id = "next", BucketId = "b", Text = "Next" };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TreeSelectionPreservesEditorUntilSaveSettlesAndReturnsAcceptedOrOriginalIds(bool saved)
    {
        var reply = new TaskCompletionSource<bool>();
        var ids = new List<string> { "next", "another" };
        var h = new Harness(() => reply.Task);
        h.Editor.SetDraft("Submitted");
        var running = h.Navigation.SelectTreeAsync(ids, new KastnSelection.Slips(ids));
        ids.Clear();
        Assert.True(h.Navigation.PreservesEditor);
        Assert.False(h.Navigation.ResolveEditor([Next], new KastnSelection.Slips([Next.Id])).Bind);
        if (saved) h.SaveCurrent();
        reply.SetResult(saved);
        var result = await running;
        Assert.NotNull(result);
        Assert.Equal(saved, result.Accepted);
        Assert.Equal(saved ? new[] { "next", "another" } : new[] { "old" }, result.Ids);
        Assert.False(h.Navigation.PreservesEditor);
        Assert.Empty(h.Projects);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("generation")]
    [InlineData("navigation")]
    [InlineData("server")]
    [InlineData("editor")]
    [InlineData("retire")]
    public async Task ContextChangesRejectBothTreeAndProjectRequestsWaitingForSave(string change)
    {
        foreach (var project in new[] { false, true })
        {
            var reply = new TaskCompletionSource<bool>();
            var h = new Harness(() => reply.Task);
            h.Editor.SetDraft("Submitted");
            var tree = project ? null : h.Navigation.SelectTreeAsync(["next"], new KastnSelection.Slips(["next"]));
            var open = project ? h.Navigation.NavigateProjectAsync("destination") : null;
            switch (change)
            {
                case "project": h.Session = h.Session with { ProjectId = "replacement" }; break;
                case "generation": h.Session = h.Session with { Generation = 2 }; break;
                case "navigation": h.Session = h.Session with { NavigationVersion = 1 }; break;
                case "server": h.Session = h.Session with { ServerId = "replacement" }; break;
                case "editor": h.Editor.Select(Next); break;
                default: h.Navigation.Retire(); break;
            }
            reply.SetResult(true);
            if (tree is not null) Assert.Null(await tree);
            if (open is not null) Assert.Equal(KastnProjectNavigationStatus.Superseded, await open);
            Assert.False(h.Navigation.PreservesEditor);
            Assert.Empty(h.Projects);
        }
    }

    [Fact]
    public async Task LatestTreeRequestWinsWithoutAnOlderFinallyClearingItsSaveWait()
    {
        var first = new TaskCompletionSource<bool>();
        var second = new TaskCompletionSource<bool>();
        var count = 0;
        var h = new Harness(() => ++count == 1 ? first.Task : second.Task);
        h.Editor.SetDraft("Submitted");
        var older = h.Navigation.SelectTreeAsync(["next"], new KastnSelection.Slips(["next"]));
        var latest = h.Navigation.SelectTreeAsync(["latest"], new KastnSelection.Slips(["latest"]));
        first.SetResult(false);
        Assert.Null(await older);
        Assert.True(h.Navigation.PreservesEditor);
        h.SaveCurrent();
        second.SetResult(true);
        Assert.Equal(new[] { "latest" }, (await latest)!.Ids);
    }

    [Fact]
    public async Task LatestProjectRequestWinsAndTreeSelectionCanSupersedeAProjectWait()
    {
        var reply = new TaskCompletionSource<bool>();
        var h = new Harness(() => reply.Task);
        h.Editor.SetDraft("Submitted");
        var older = h.Navigation.NavigateProjectAsync("first");
        var latest = h.Navigation.NavigateProjectAsync("second");
        h.SaveCurrent();
        reply.SetResult(true);
        Assert.Equal(KastnProjectNavigationStatus.Superseded, await older);
        Assert.Equal(KastnProjectNavigationStatus.Completed, await latest);
        Assert.Equal(new[] { "second" }, h.Projects);

        reply = new();
        h = new(() => reply.Task);
        h.Editor.SetDraft("Submitted");
        older = h.Navigation.NavigateProjectAsync("first");
        var tree = h.Navigation.SelectTreeAsync(["next"], new KastnSelection.Slips(["next"]));
        h.SaveCurrent();
        reply.SetResult(true);
        Assert.Equal(KastnProjectNavigationStatus.Superseded, await older);
        Assert.True((await tree)!.Accepted);
        Assert.Empty(h.Projects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterTypingBlocksNavigationEvenWhenTheSaveCallbackSaysSuccess(bool project)
    {
        var reply = new TaskCompletionSource<bool>();
        var h = new Harness(() => reply.Task);
        h.Editor.SetDraft("Submitted");
        var tree = project ? null : h.Navigation.SelectTreeAsync(["next"], new KastnSelection.Slips(["next"]));
        var open = project ? h.Navigation.NavigateProjectAsync("destination") : null;
        h.SaveCurrent();
        h.Editor.SetDraft("Later writing");
        reply.SetResult(true);
        if (tree is not null) Assert.False((await tree)!.Accepted);
        if (open is not null) Assert.Equal(KastnProjectNavigationStatus.SaveBlocked, await open);
        Assert.Equal("Later writing", h.Editor.DraftText);
        Assert.Empty(h.Projects);
    }

    [Fact]
    public async Task SameEditedSlipDoesNotSaveAndASelectionBackCancelsAnOlderWait()
    {
        var reply = new TaskCompletionSource<bool>();
        var saves = 0;
        var h = new Harness(() => { saves++; return reply.Task; });
        h.Editor.SetDraft("Submitted");
        var older = h.Navigation.SelectTreeAsync(["next"], new KastnSelection.Slips(["next"]));
        var back = await h.Navigation.SelectTreeAsync(["old"], new KastnSelection.Slips(["old"]));
        Assert.True(back!.Accepted);
        Assert.Equal(1, saves);
        Assert.False(h.Navigation.PreservesEditor);
        reply.SetResult(false);
        Assert.Null(await older);
    }

    [Fact]
    public void PendingFocusIsConsumedOnceAndDoesNotCarryToAnotherRequestOrSession()
    {
        var h = new Harness();
        h.Navigation.RequestBucket("b");
        h.Navigation.RequestSlip("next", focus: true);
        Assert.Equal(new[] { "next" }, h.Navigation.RestoreTreeSelection(["old"], "a", _ => true, "old", "a"));
        Assert.Null(h.Navigation.PendingBucketId);
        var first = h.Navigation.ResolveEditor([Old, Next], new KastnSelection.None());
        Assert.True(first.Bind && first.Focus);
        Assert.Same(Next, first.Slip);
        Assert.False(h.Navigation.ResolveEditor([Old, Next], new KastnSelection.None()).Focus);
        h.Navigation.RequestSlip("missing", focus: true);
        h.Navigation.RequestSlip("next");
        Assert.False(h.Navigation.ResolveEditor([Next], new KastnSelection.None()).Focus);
        h.Navigation.RequestSlip("next", focus: true);
        h.Navigation.ObserveSession("other", 2);
        Assert.Null(h.Navigation.PendingSlipId);
        Assert.False(h.Navigation.ResolveEditor([Next], new KastnSelection.None()).Focus);
    }

    [Fact]
    public void RestoringSelectionKeepsSurvivingBatchAndUsesBucketThenSlipFallback()
    {
        var h = new Harness();
        h.Navigation.RequestBucket("requested");
        Assert.Equal(new[] { "second", "first" }, h.Navigation.RestoreTreeSelection(["second", "gone", "first"], "a", id => id != "gone", "old", "a"));
        h.Navigation.RequestBucket("requested");
        Assert.Equal(new[] { "requested" }, h.Navigation.RestoreTreeSelection([], "a", _ => false, "old", "a"));
        Assert.Equal(new[] { "old" }, h.Navigation.RestoreTreeSelection([], null, _ => false, "old", "a"));
        Assert.Equal(new[] { "a" }, h.Navigation.RestoreTreeSelection([], null, _ => false, null, "a"));
        Assert.Empty(h.Navigation.RestoreTreeSelection([], null, _ => false, null, null));
    }

    [Fact]
    public void EditorBindingPreservesDirtyConflictAndTitleTargetsButClearsBatches()
    {
        var h = new Harness();
        Assert.False(h.Navigation.ResolveEditor([Old], new KastnSelection.None()).Bind);
        Assert.True(h.Navigation.ResolveEditor([Next], new KastnSelection.None()).Bind);
        Assert.False(h.Navigation.ResolveEditor([Next], new KastnSelection.BucketTitle("b")).Bind);
        h.Editor.SetDraft("Unsaved");
        Assert.False(h.Navigation.ResolveEditor([Next], new KastnSelection.None()).Bind);
        h.Editor.Reconcile(Old with { Text = "Remote", Revision = 3 });
        Assert.NotNull(h.Editor.ConflictCurrent);
        Assert.False(h.Navigation.ResolveEditor([Next], new KastnSelection.None()).Bind);
        var batch = h.Navigation.ResolveEditor([Old, Next], new KastnSelection.Slips([Old.Id, Next.Id]));
        Assert.True(batch.Bind);
        Assert.Null(batch.Slip);
    }

    [Fact]
    public async Task PreparedProjectHandoffDoesNotSaveAgainAndRetirementRejectsNewRequests()
    {
        var saves = 0;
        var h = new Harness(() => { saves++; return Task.FromResult(true); });
        Assert.Equal(KastnProjectNavigationStatus.Completed, await h.Navigation.NavigateProjectAsync("created", alreadySaved: true));
        Assert.Equal(0, saves);
        h.Navigation.Retire();
        h.Navigation.RequestSlip("next", focus: true);
        h.Navigation.RequestBucket("b");
        Assert.Null(h.Navigation.PendingSlipId);
        Assert.Null(h.Navigation.PendingBucketId);
        Assert.Equal(KastnProjectNavigationStatus.Superseded, await h.Navigation.NavigateProjectAsync(null));
        Assert.Single(h.Projects);
    }

    private sealed class Harness
    {
        public KastnEditorState Editor { get; } = new();
        public KastnNavigationSession Session = new("project", 1, 0, "server", false);
        public List<string?> Projects { get; } = [];
        public KastnNavigationCoordinator Navigation { get; }
        public Harness(Func<Task<bool>>? save = null)
        {
            Editor.Select(Old);
            Navigation = new(Editor, () => Session, save ?? (() => Task.FromResult(true)), id =>
            {
                Projects.Add(id);
                Session = Session with { ProjectId = id, Generation = Session.Generation + 1, NavigationVersion = Session.NavigationVersion + 1 };
                Navigation!.ObserveSession(id, Session.Generation);
                return Task.CompletedTask;
            });
            Navigation.ObserveSession(Session.ProjectId, Session.Generation);
        }
        public void SaveCurrent() => Editor.AcceptSaved(Old with { Text = Editor.DraftText, Revision = 2 });
    }
}
