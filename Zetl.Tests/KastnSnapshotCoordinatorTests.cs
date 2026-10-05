using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public class KastnSnapshotCoordinatorTests
{
    [Fact]
    public void LatestDeliveryWinsAcrossPostedAndSynchronousUpdates()
    {
        var h = new Harness();
        h.Owner.Queue(Snapshot("a", 1));
        h.Owner.Queue(Snapshot("a", 2));
        Assert.Single(h.Posts);
        h.Owner.ApplyNow(Snapshot("a", 3));
        h.Owner.Queue(Snapshot("a", 1));
        h.Drain();
        Assert.Equal(new long[] { 3 }, h.Rendered);
        h.Owner.Queue(Snapshot("a", 3)); // Selection synchronization may reuse a publication.
        h.Drain();
        Assert.Equal(new long[] { 3, 3 }, h.Rendered);
        Assert.Equal(1, h.Resets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoalescedRoundTripsStillRetireProjectAndServerScopes(bool server)
    {
        var h = new Harness();
        h.Owner.ApplyNow(Snapshot("a", 1, "one"));
        h.Plans.Clear();
        h.Owner.Queue(Snapshot(server ? "a" : "b", 2, server ? "two" : "one"));
        h.Owner.Queue(Snapshot("a", 3, "one"));
        h.Drain();
        var plan = Assert.Single(h.Plans);
        Assert.False(plan.ProjectChanged);
        Assert.Equal(!server, plan.ProjectScopeChanged);
        Assert.Equal(server, plan.ServerChanged);
        Assert.Equal(2, h.Resets);
        Assert.Equal(new long[] { 1, 3 }, h.Rendered);
    }

    [Fact]
    public void NavigationIntentRejectsOldQueueAndPreservesUnobservedRoundTrip()
    {
        var h = new Harness();
        h.Owner.ApplyNow(Snapshot("a", 1));
        h.Owner.Queue(Snapshot("a", 2));
        h.Navigation = 2;
        h.Drain();
        h.Owner.Queue(Snapshot("b", 3) with { NavigationVersion = 1 });
        h.Owner.Queue(Snapshot("a", 4) with { NavigationVersion = 2 });
        h.Drain();
        Assert.Equal(new long[] { 1, 4 }, h.Rendered);
        Assert.True(h.Plans.Last().ProjectScopeChanged);
    }

    [Fact]
    public void RecoveryAndAcknowledgementPrecedePresentationAndUpdateScopeIsRestored()
    {
        var h = new Harness();
        h.Owner.ApplyNow(Snapshot("a", 1));
        Assert.Equal(new[] { "enter", "observe", "capture", "reset", "projects", "adopt", "projection",
            "ack", "editor", "bind", "draft", "present", "connection", "exit" }, h.Calls);
        Assert.Equal("recovered writing", h.PresentedText);
        Assert.False(h.Updating);
    }

    [Fact]
    public void FailedApplicationRestoresGuardAndCanRetry()
    {
        var h = new Harness { FailProjection = true };
        Assert.Throws<InvalidOperationException>(() => h.Owner.ApplyNow(Snapshot("a", 1)));
        Assert.False(h.Updating);
        Assert.Empty(h.Rendered);
        h.FailProjection = false;
        h.Owner.ApplyNow(Snapshot("a", 1));
        Assert.True(h.Plans.Last().ProjectChanged);
        Assert.Single(h.Rendered);
    }

    [Fact]
    public void ReentrantApplicationIsQueuedAndRetirementCancelsOutstandingWork()
    {
        var h = new Harness();
        h.OnProjection = () =>
        {
            h.OnProjection = null;
            h.Owner.Queue(Snapshot("a", 2));
            h.Drain(); // A native callback may process nested dispatcher work.
            h.Owner.ApplyNow(Snapshot("a", 3));
        };
        h.Owner.ApplyNow(Snapshot("a", 1));
        Assert.Equal(new long[] { 1 }, h.Rendered);
        h.Drain();
        Assert.Equal(new long[] { 1, 3 }, h.Rendered);
        h.Owner.Queue(Snapshot("a", 4));
        h.Owner.Retire();
        h.Drain();
        h.Owner.ApplyNow(Snapshot("a", 5));
        Assert.Equal(new long[] { 1, 3 }, h.Rendered);
    }

    [Fact]
    public void CoalescedProjectRoundTripRetiresAcceptanceWithoutDiscardingCurrentWriting()
    {
        var h = new Harness { Recover = false };
        var first = Snapshot("a", 1);
        h.Owner.ApplyNow(first);
        h.Editor.Select(first.Project!.Slips[0]);
        h.Editor.SetDraft("later writing");
        var token = h.Editor.SelectionVersion;
        h.Owner.Queue(Snapshot("b", 2));
        h.Owner.Queue(Snapshot("a", 3));
        h.Drain();
        Assert.True(h.Editor.SelectionVersion > token);
        Assert.Equal("later writing", h.Editor.DraftText);
        Assert.Null(h.Editor.ConflictCurrent);
        Assert.True(h.Editor.IsDirty);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ServerReplacementAcceptsLowerRevisionsAndProtectsWriting(bool dirty, bool changed)
    {
        var h = new Harness { Recover = false };
        var first = Snapshot("a", 1, "one");
        h.Owner.ApplyNow(first);
        h.Editor.Select(first.Project!.Slips[0]);
        if (dirty) h.Editor.SetDraft("local writing");
        var token = h.Editor.SelectionVersion;
        var replacement = first.Project.Slips[0] with { Revision = 1, Text = changed ? "restored content" : "baseline" };
        h.Owner.ApplyNow(Snapshot("a", 2, "two") with { Project = first.Project with { Slips = [replacement] } });
        Assert.True(h.Editor.SelectionVersion > token);
        Assert.Equal(dirty ? "local writing" : "baseline", h.Editor.DraftText);
        Assert.Equal(dirty && changed ? replacement : null, h.Editor.ConflictCurrent);
        Assert.Equal(changed ? 8 : 1, h.Editor.Revision);
    }

    private static KastnSessionSnapshot Snapshot(string projectId, long publication, string server = "one") =>
        new(KastnConnectionState.Online, "Connected", [], new()
        {
            Id = projectId, Name = projectId, MetadataRevision = 1, ChangeSequence = publication,
            Slips = [new() { Id = "slip", BucketId = "bucket", Text = "baseline", Revision = 8,
                Type = ZetlSlipType.Text, Source = "kastn", CapturedAtUtc = DateTimeOffset.UtcNow }]
        }) { PublicationVersion = publication, ServerInstanceId = server };

    private sealed class Harness
    {
        public readonly KastnEditorState Editor = new();
        public readonly Queue<Action> Posts = new();
        public readonly List<string> Calls = [];
        public readonly List<KastnSnapshotPlan> Plans = [];
        public readonly List<long> Rendered = [];
        public readonly KastnSnapshotCoordinator Owner;
        public long Navigation;
        public int Resets;
        public bool Updating, FailProjection, Recover = true;
        public Action? OnProjection;
        public string? PresentedText;
        private long publication;
        public Harness()
        {
            Owner = new(Editor, () => Navigation, Posts.Enqueue, new(
                () => { Calls.Add("enter"); Updating = true; return new Scope(() => { Updating = false; Calls.Add("exit"); }); },
                plan => { Calls.Add("observe"); Plans.Add(plan); publication = plan.Snapshot.PublicationVersion; },
                () => { Calls.Add("capture"); return new(null, "slip", null); },
                () => { Calls.Add("reset"); Resets++; },
                _ => Calls.Add("projects"),
                (_, selection) => { Calls.Add("adopt"); return selection; },
                (_, _) => { Calls.Add("projection"); if (FailProjection) throw new InvalidOperationException(); OnProjection?.Invoke(); },
                (_, _) => { Calls.Add("ack"); return false; },
                () => Calls.Add("editor"), () => Calls.Add("bind"),
                project => { Calls.Add("draft"); if (Recover) { Editor.Select(project.Slips[0]); Editor.SetDraft("recovered writing"); } },
                () => { Calls.Add("present"); PresentedText = Editor.DraftText; Rendered.Add(publication); },
                _ => Calls.Add("connection")));
        }
        public void Drain() { while (Posts.TryDequeue(out var post)) post(); }
    }
    private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
