using ZETL.Contracts;

namespace KASTN;

internal enum KastnMutationPreparation { Visibility, BoardEdit, Drop, AddSlip, Move, Delete, Restore, History }

// UI-thread owner for exclusive writes, workflow reservations and the shared
// editor save. Leases release only their own state; native visual delay remains
// in the window. Retirement prevents new work without cancelling sent commands.
internal sealed class KastnMutationCoordinator(Action changed)
{
    private readonly HashSet<KastnMutationPreparation> preparations = [];
    private IDisposable? writer;
    private readonly AsyncLocal<IDisposable?> inheritedWriter = new();
    private bool commandRunning;
    private bool retired;
    private (KastnNavigationSession Session, long Intent, long Sequence, string Message)? notice;
    public bool IsBusy => writer is not null;
    public Task<bool>? InflightSave { get; private set; }
    public bool IsSaving => InflightSave is { IsCompleted: false };
    public bool IsPreparing(KastnMutationPreparation kind) => preparations.Contains(kind);

    public IDisposable? TryPrepare(KastnMutationPreparation kind)
    {
        if (retired || IsBusy && !IsSaving || !preparations.Add(kind)) return null;
        var lease = new Lease(() => { if (preparations.Remove(kind) && !retired) changed(); });
        changed();
        return lease;
    }

    public IDisposable? TryBeginWrite()
    {
        if (retired || IsBusy) return null;
        IDisposable? lease = null;
        lease = new Lease(() =>
        {
            if (!ReferenceEquals(writer, lease)) return;
            writer = null;
            if (ReferenceEquals(inheritedWriter.Value, lease)) inheritedWriter.Value = null;
            if (!retired) changed();
        });
        writer = lease;
        notice = null;
        inheritedWriter.Value = lease;
        changed();
        return lease;
    }

    public void SetNotice(KastnSessionSnapshot snapshot, KastnNavigationSession session, long intent, string message)
    {
        if (!retired && snapshot.Project is { } project && project.Id == session.ProjectId)
            notice = (session, intent, project.ChangeSequence, message);
    }

    public string? NoticeFor(KastnSessionSnapshot snapshot, KastnSessionSnapshot latest, KastnNavigationSession session, long intent)
    {
        if (notice is not { } value) return null;
        bool Matches(KastnSessionSnapshot candidate) => candidate.ConnectionState == KastnConnectionState.Online
            && candidate.Project is { } project && project.Id == session.ProjectId && project.ChangeSequence == value.Sequence
            && candidate.ServerInstanceId == session.ServerId;
        if (retired || session != value.Session || intent != value.Intent || !Matches(latest))
        { notice = null; return null; }
        return Matches(snapshot) ? value.Message : null;
    }

    public async Task<ZetlResponseEnvelope> ExecuteAsync(ZetlCommandEnvelope command,
        Func<ZetlCommandEnvelope, Task<ZetlResponseEnvelope>> execute)
    {
        if (retired || commandRunning || IsBusy && !ReferenceEquals(writer, inheritedWriter.Value))
            return new() { CommandId = command.CommandId, ProjectId = command.ProjectId, Status = ZetlResponseStatus.Failure,
                Error = new() { Code = "busy", Message = "Wait for the current edit to finish." } };
        // A workflow scope holds busy through its final refresh. Standalone
        // commands acquire their own scope; concurrent commands never queue stale
        // target snapshots behind another writer.
        using var ownScope = IsBusy ? null : TryBeginWrite();
        commandRunning = true;
        try { return await execute(command); }
        finally { commandRunning = false; }
    }

    public Task<bool> SaveAsync(Func<Task<bool>> save)
    {
        if (retired) return Task.FromResult(false);
        if (InflightSave is { IsCompleted: false } pending) return pending;
        // Publish before invoking the factory: focus loss can reenter saving
        // synchronously while the factory updates native controls.
        var completion = new TaskCompletionSource<bool>();
        InflightSave = completion.Task;
        _ = CompleteSaveAsync(save, completion);
        return completion.Task;
    }

    private async Task CompleteSaveAsync(Func<Task<bool>> save, TaskCompletionSource<bool> completion)
    {
        try { completion.TrySetResult(await save() && !retired); }
        catch (Exception ex) { completion.TrySetException(ex); }
    }

    public void Retire()
    {
        retired = true;
        preparations.Clear();
        notice = null;
        writer = null;
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? current = release;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Invoke();
    }
}

// Captures the source before prerequisite saves/dialogs. Later draft text is
// allowed once commands are sent; navigation, selection and session replacement
// stop further commands. Empty-editor refreshes do not create a new user intent.
internal sealed class KastnMutationContext(KastnNavigationSession session, long intent, KastnEditorState editor)
{
    private readonly string? slipId = editor.SlipId;
    private readonly long editorVersion = editor.SelectionVersion;
    public string ProjectId => session.ProjectId!;
    public bool MatchesScope(KastnNavigationSession current, long currentIntent) =>
        !session.Retired && current == session && currentIntent == intent;
    public bool Matches(KastnNavigationSession current, long currentIntent, KastnEditorState currentEditor) =>
        MatchesScope(current, currentIntent)
        && currentEditor.SlipId == slipId
        && (slipId is null || currentEditor.SelectionVersion == editorVersion);
}
