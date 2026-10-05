using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public class KastnMutationCoordinatorTests
{
    private static ZetlCommandEnvelope Command(string id = "one") => ZetlCommandEnvelope.Create(id,
        ZetlCommandKind.UpdateSlip, new UpdateSlipCommand { Text = "saved" }, "project", id, 1);
    private static ZetlResponseEnvelope Success(ZetlCommandEnvelope command) => new()
        { CommandId = command.CommandId, Status = ZetlResponseStatus.Success };

    [Fact]
    public async Task SaveIsPublishedBeforeFactoryReentryAndRetriesAfterCompletion()
    {
        var h = new KastnMutationCoordinator(() => { });
        var reply = new TaskCompletionSource<bool>();
        Task<bool>? nested = null;
        var running = h.SaveAsync(() =>
        {
            nested = h.SaveAsync(() => throw new InvalidOperationException("Duplicate save."));
            return reply.Task;
        });
        Assert.Same(running, nested);
        Assert.True(h.IsSaving);
        reply.SetResult(true);
        Assert.True(await running);
        Assert.False(h.IsSaving);
        Assert.False(await h.SaveAsync(() => Task.FromResult(false)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFactoryFailureAndCancellationDoNotPoisonTheNextSave(bool cancelled)
    {
        var h = new KastnMutationCoordinator(() => { });
        var failed = h.SaveAsync(() => cancelled ? Task.FromCanceled<bool>(new CancellationToken(true))
            : throw new IOException("save failed"));
        await Assert.ThrowsAnyAsync<Exception>(() => failed);
        Assert.True(await h.SaveAsync(() => Task.FromResult(true)));
    }

    [Fact]
    public async Task ExclusiveWriteReservationsAndReleasesDoNotClearAnotherWorkflow()
    {
        var h = new KastnMutationCoordinator(() => { });
        using var board = h.TryPrepare(KastnMutationPreparation.BoardEdit);
        Assert.NotNull(board);
        Assert.Null(h.TryPrepare(KastnMutationPreparation.BoardEdit));
        var first = h.TryBeginWrite();
        Assert.NotNull(first);
        Assert.Null(h.TryBeginWrite());
        Assert.Null(h.TryPrepare(KastnMutationPreparation.Drop));
        first.Dispose();
        using var second = h.TryBeginWrite();
        Assert.NotNull(second);
        first.Dispose();
        board.Dispose();
        Assert.True(h.IsBusy);
        Assert.False(h.IsPreparing(KastnMutationPreparation.BoardEdit));
        // A different execution context cannot borrow the active writer.
        Task<ZetlResponseEnvelope> foreign;
        using (ExecutionContext.SuppressFlow()) foreign = Task.Run(() => h.ExecuteAsync(Command(),
            _ => throw new InvalidOperationException("Foreign command was submitted.")));
        Assert.Equal(ZetlResponseStatus.Failure, (await foreign).Status);
        Assert.True(h.IsBusy);
    }

    [Fact]
    public async Task CommandsKeepWorkflowBusyAndRejectConcurrentSubmissionsWithoutQueueing()
    {
        var h = new KastnMutationCoordinator(() => { });
        using var scope = h.TryBeginWrite();
        var reply = new TaskCompletionSource<ZetlResponseEnvelope>();
        var command = Command();
        var first = h.ExecuteAsync(command, _ => reply.Task);
        Assert.Equal(ZetlResponseStatus.Failure, (await h.ExecuteAsync(Command("two"),
            _ => throw new InvalidOperationException("Overlapping command."))).Status);
        reply.SetResult(Success(command));
        await first;
        Assert.True(h.IsBusy);
        Assert.Equal(ZetlResponseStatus.Success, (await h.ExecuteAsync(Command("next"), c => Task.FromResult(Success(c)))).Status);
    }

    [Fact]
    public async Task StandaloneCommandFailureReleasesBusyAndPermitsRetry()
    {
        var h = new KastnMutationCoordinator(() => { });
        await Assert.ThrowsAsync<IOException>(() => h.ExecuteAsync(Command(), _ => throw new IOException("Transport failed.")));
        Assert.False(h.IsBusy);
        Assert.Equal(ZetlResponseStatus.Success, (await h.ExecuteAsync(Command(), c => Task.FromResult(Success(c)))).Status);
        Assert.False(h.IsBusy);
    }

    [Fact]
    public async Task RetirementStopsNewWorkAndLateCompletionsDoNotTouchUiState()
    {
        var changes = 0;
        var h = new KastnMutationCoordinator(() => changes++);
        var reply = new TaskCompletionSource<bool>();
        IDisposable? scope = null;
        var saving = h.SaveAsync(() => { scope = h.TryBeginWrite(); return reply.Task; });
        using var joining = h.TryPrepare(KastnMutationPreparation.Drop);
        Assert.NotNull(joining);
        h.Retire();
        var before = changes;
        scope!.Dispose();
        joining.Dispose();
        reply.SetResult(true);
        Assert.False(await saving);
        Assert.Null(h.TryBeginWrite());
        Assert.Null(h.TryPrepare(KastnMutationPreparation.Move));
        Assert.False(await h.SaveAsync(() => throw new InvalidOperationException("Retired save.")));
        Assert.Equal(before, changes);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("generation")]
    [InlineData("server")]
    [InlineData("navigation")]
    [InlineData("intent")]
    [InlineData("editor")]
    [InlineData("retire")]
    public void MutationContextRejectsReplacedTargetsAndAllowsLaterTyping(string change)
    {
        var editor = new KastnEditorState();
        editor.Select(Slip("one"));
        var session = new KastnNavigationSession("project", 1, 2, "server", false);
        var h = new KastnMutationContext(session, 3, editor);
        editor.SetDraft("Later writing");
        Assert.True(h.Matches(session, 3, editor));
        var intent = 3;
        switch (change)
        {
            case "project": session = session with { ProjectId = "other" }; break;
            case "generation": session = session with { Generation = 2 }; break;
            case "server": session = session with { ServerId = "other" }; break;
            case "navigation": session = session with { NavigationVersion = 3 }; break;
            case "intent": intent++; break;
            case "editor": editor.Select(Slip("one")); break;
            default: session = session with { Retired = true }; break;
        }
        Assert.False(h.Matches(session, intent, editor));
    }

    [Fact]
    public async Task SlipBatchKeepsCapturedOrderCountsFailuresAndSkipsAndStopsAfterInvalidation()
    {
        var targets = new List<ZetlSlipSnapshot> { Slip("one"), Slip("skip"), Slip("fail"), Slip("last") };
        var h = new KastnSlipMutationBatch("project", targets);
        targets.Clear();
        var sent = new List<string>();
        var accepted = new List<string>();
        var result = await h.ExecuteAsync((slip, _) => slip.Id == "skip" ? null : Command(slip.Id), command =>
        {
            sent.Add(command.TargetId!);
            return Task.FromResult(command.TargetId == "fail"
                ? new ZetlResponseEnvelope { CommandId = command.CommandId, Status = ZetlResponseStatus.Conflict }
                : Success(command) with { Payload = ZetlProtocolJson.ToElement(Slip(command.TargetId!) with { Revision = 2 }) });
        }, () => true, saved => accepted.Add(saved.Id));
        Assert.Equal(new[] { "one", "fail", "last" }, sent);
        Assert.Equal(new[] { "one", "last" }, accepted);
        Assert.Equal(new KastnSlipBatchResult(2, 1, 1, false), result);
        var current = true;
        result = await new KastnSlipMutationBatch("project", [Slip("one"), Slip("next")]).ExecuteAsync((slip, _) => Command(slip.Id), c =>
        {
            current = false;
            return Task.FromResult(Success(c) with { Payload = ZetlProtocolJson.ToElement(Slip("one") with { Revision = 2 }) });
        }, () => current, _ => throw new InvalidOperationException("Stale completion UI."));
        Assert.Equal(new KastnSlipBatchResult(1, 0, 0, true), result);
    }

    private static ZetlSlipSnapshot Slip(string id) => new()
    { Id = id, BucketId = "bucket", Revision = 1, Type = ZetlSlipType.Text, Text = id, Source = "copy", CapturedAtUtc = DateTimeOffset.UtcNow };
}
