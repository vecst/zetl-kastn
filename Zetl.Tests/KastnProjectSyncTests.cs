using System.Text.Json;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

// KastnProjectSync against an in-process Zetl service, with replies a test can
// tamper with: the recovery paths real IPC never produces on demand.
public sealed class KastnProjectSyncTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "KastnProjectSyncTests", Guid.NewGuid().ToString("N"));
    private readonly ZetlStateStore store;
    private readonly ZetlProject project;
    private readonly ZetlProjectService service;
    private readonly List<ZetlCommandEnvelope> sent = [];

    public KastnProjectSyncTests()
    {
        Directory.CreateDirectory(directory);
        store = new ZetlStateStore(Path.Combine(directory, "state.json"), "kastn-sync");
        project = store.CreateProject("Sync", ["Inbox"], "Inbox");
        store.AddSlip(project.Buckets[0], "first", "fixture");
        service = new ZetlProjectService(store);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public async Task AMalformedBatchGetsOneFreshBootstrap()
    {
        var sync = new KastnProjectSync();
        var first = await sync.ReadAsync(Send(tamperDeltas: 1), deltasOffered: true, project.Id, default);
        sync.Commit(first, Summaries());
        store.AddSlip(project.Buckets[0], "second", "fixture");

        var read = await sync.ReadAsync(Send(tamperDeltas: 1), deltasOffered: true, project.Id, default);

        Assert.Equal(FullJson(), Json(read.Snapshot));
        Assert.Equal("bootstrap", read.ResetReason);
        var changeReads = sent.Where(command => command.Kind == ZetlCommandKind.GetProjectChanges).Select(CursorOf).ToList();
        // First load, the tampered delta from its cursor, then one fresh bootstrap.
        Assert.Equal(3, changeReads.Count);
        Assert.Null(changeReads[0]);
        Assert.NotNull(changeReads[1]);
        Assert.Null(changeReads[2]);
    }

    [Fact]
    public async Task ABootstrapThatAlsoFailsFailsTheRead()
    {
        var sync = new KastnProjectSync();
        var first = await sync.ReadAsync(Send(), deltasOffered: true, project.Id, default);
        sync.Commit(first, Summaries());
        store.AddSlip(project.Buckets[0], "second", "fixture");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            sync.ReadAsync(Send(tamperDeltas: 1, tamperBootstraps: 1), deltasOffered: true, project.Id, default));
    }

    [Fact]
    public async Task AnOlderZetlGetsWholeProjectReads()
    {
        var read = await new KastnProjectSync().ReadAsync(Send(), deltasOffered: false, project.Id, default);

        Assert.Equal(FullJson(), Json(read.Snapshot));
        Assert.Null(read.Cursor);
        Assert.All(sent, command => Assert.Equal(ZetlCommandKind.GetProject, command.Kind));
    }

    // Replies straight from the service, except that the first
    // <paramref name="tamperDeltas"/> delta replies and <paramref name="tamperBootstraps"/>
    // bootstrap replies are corrupted so they cannot apply.
    private Func<ZetlCommandEnvelope, CancellationToken, Task<ZetlResponseEnvelope>> Send(
        int tamperDeltas = 0, int tamperBootstraps = 0) => (command, _) =>
    {
        sent.Add(command);
        var response = service.Execute(command);
        if (command.Kind != ZetlCommandKind.GetProjectChanges) return Task.FromResult(response);
        var changes = response.Payload!.Value.Deserialize<ZetlProjectChanges>(ZetlProtocolJson.Options)!;
        if (changes.Snapshot is null && tamperDeltas-- > 0)
        {
            changes = changes with { FromCursor = changes.FromCursor! with { Token = "not-the-applied-cursor" } };
        }
        else if (changes.Snapshot is not null && CursorOf(command) is null && tamperBootstraps-- > 0)
        {
            changes = changes with { Snapshot = changes.Snapshot with { ChangeSequence = changes.Snapshot.ChangeSequence + 99 } };
        }

        return Task.FromResult(response with { Payload = ZetlProtocolJson.ToElement(changes) });
    };

    private IReadOnlyList<ZetlProjectSummary> Summaries() =>
        service.Execute(new ZetlCommandEnvelope { CommandId = Guid.NewGuid().ToString("N"), Kind = ZetlCommandKind.ListProjects })
            .Payload!.Value.Deserialize<List<ZetlProjectSummary>>(ZetlProtocolJson.Options)!;

    private string FullJson() => service.Execute(new ZetlCommandEnvelope
    {
        CommandId = Guid.NewGuid().ToString("N"),
        Kind = ZetlCommandKind.GetProject,
        ProjectId = project.Id
    }).Payload!.Value.GetRawText();

    private static string Json(ZetlProjectSnapshot snapshot) => JsonSerializer.Serialize(snapshot, ZetlProtocolJson.Options);

    private static ZetlProjectCursor? CursorOf(ZetlCommandEnvelope command) =>
        command.Payload?.Deserialize<GetProjectChangesCommand>(ZetlProtocolJson.Options)?.Cursor;
}
