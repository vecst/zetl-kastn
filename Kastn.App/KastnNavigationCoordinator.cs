using ZETL.Contracts;

namespace KASTN;

internal sealed record KastnNavigationSession(string? ProjectId, long Generation,
    long NavigationVersion, string? ServerId, bool Retired);
internal enum KastnProjectNavigationStatus { Completed, SaveBlocked, Superseded }
internal sealed record KastnTreeNavigationResult(IReadOnlyList<string> Ids, bool Accepted);
internal sealed record KastnEditorNavigationResult(bool Bind, ZetlSlipSnapshot? Slip = null, bool Focus = false);

// Owns navigation intent and editor-binding policy, independently of controls.
// The window applies tree selection, pane changes and native focus. Saves remain
// shared with mutation workflows; project transport remains connection-owned.
internal sealed class KastnNavigationCoordinator(
    KastnEditorState editor,
    Func<KastnNavigationSession> currentSession,
    Func<Task<bool>> saveEditor,
    Func<string?, Task> navigateProject)
{
    private sealed record LeaveRequest(long Intent, KastnNavigationSession Session, long EditorVersion);
    private LeaveRequest? pendingLeave;
    private long intent;
    private bool retired;
    private (string? ProjectId, long Generation)? observedScope;
    public string? PendingBucketId { get; private set; }
    public string? PendingSlipId { get; private set; }
    private bool pendingFocus;

    private bool IsCurrent(LeaveRequest request) => !retired && request.Intent == intent
        && currentSession() == request.Session && !request.Session.Retired
        && editor.SelectionVersion == request.EditorVersion;

    public bool PreservesEditor => pendingLeave is { } request && IsCurrent(request);

    public void ObserveSession(string? projectId, long generation)
    {
        var scope = (projectId, generation);
        if (observedScope == scope) return;
        observedScope = scope;
        ClearSelectionRequests();
        if (pendingLeave is { } request && (request.Session.ProjectId != projectId || request.Session.Generation != generation))
            pendingLeave = null;
    }

    public void RequestBucket(string? id)
    {
        if (!retired && !currentSession().Retired) PendingBucketId = id;
    }
    public void RequestSlip(string id, bool focus = false)
    {
        if (retired || currentSession().Retired) return;
        PendingSlipId = id;
        pendingFocus = focus;
    }
    public void ClearSlipRequest() { PendingSlipId = null; pendingFocus = false; }
    public void ClearSelectionRequests() { PendingBucketId = null; ClearSlipRequest(); }

    public void Retire()
    {
        retired = true;
        intent++;
        pendingLeave = null;
        ClearSelectionRequests();
    }

    public IReadOnlyList<string> RestoreTreeSelection(IReadOnlyList<string> remembered, string? bucketId,
        Func<string, bool> contains, string? firstSlipId, string? firstBucketId)
    {
        var requestedBucket = PendingBucketId ?? bucketId;
        PendingBucketId = null;
        if (PendingSlipId is { } pending && contains(pending)) return [pending];
        var present = remembered.Where(contains).ToArray();
        if (present.Length > 0) return present;
        return (requestedBucket ?? firstSlipId ?? firstBucketId) is { } fallback ? [fallback] : [];
    }

    public async Task<KastnTreeNavigationResult?> SelectTreeAsync(IReadOnlyList<string> ids, KastnSelection selection)
    {
        var request = BeginIntent();
        if (!IsCurrent(request)) return null;
        var requested = ids.ToArray();
        var singleId = selection is KastnSelection.Slips { SlipIds: [var only] } ? only : null;
        if (editor.SlipId is not { } editingId || editingId == singleId || !editor.IsDirty)
            return new(requested, true);
        pendingLeave = request;
        try
        {
            var saved = await saveEditor();
            if (!IsCurrent(request)) return null;
            return saved && !editor.IsDirty && editor.ConflictCurrent is null
                ? new(requested, true) : new([editingId], false);
        }
        finally { if (ReferenceEquals(pendingLeave, request)) pendingLeave = null; }
    }

    public KastnEditorNavigationResult ResolveEditor(IReadOnlyList<ZetlSlipSnapshot> filtered, KastnSelection selection)
    {
        if (PreservesEditor) return new(false);
        var selected = filtered.FirstOrDefault(slip => slip.Id == (PendingSlipId ?? editor.SlipId));
        if (PendingSlipId is null && selection is KastnSelection.Slips { SlipIds.Count: > 1 })
            return new(true);
        if (selected is not null)
        {
            if (PendingSlipId != selected.Id) return new(false);
            var focus = pendingFocus;
            ClearSlipRequest();
            return new(true, selected, focus);
        }
        return !editor.IsDirty && editor.ConflictCurrent is null && selection is not KastnSelection.BucketTitle
            ? new(true, filtered.FirstOrDefault()) : new(false);
    }

    public async Task<KastnProjectNavigationStatus> NavigateProjectAsync(string? projectId,
        bool alreadySaved = false, Action? beforeNavigate = null)
    {
        var request = BeginIntent();
        if (!IsCurrent(request)) return KastnProjectNavigationStatus.Superseded;
        pendingLeave = request;
        try
        {
            var saved = alreadySaved || await saveEditor();
            if (!IsCurrent(request)) return KastnProjectNavigationStatus.Superseded;
            if (!saved || editor.IsDirty || editor.ConflictCurrent is not null)
                return KastnProjectNavigationStatus.SaveBlocked;
            pendingLeave = null;
            ClearSelectionRequests();
            beforeNavigate?.Invoke();
            await navigateProject(projectId);
            var current = currentSession();
            return !retired && !current.Retired && intent == request.Intent
                && current.NavigationVersion == request.Session.NavigationVersion + 1
                && current.ServerId == request.Session.ServerId
                    ? KastnProjectNavigationStatus.Completed : KastnProjectNavigationStatus.Superseded;
        }
        finally { if (ReferenceEquals(pendingLeave, request)) pendingLeave = null; }
    }

    private LeaveRequest BeginIntent()
    {
        pendingLeave = null;
        ClearSelectionRequests();
        return new(++intent, currentSession(), editor.SelectionVersion);
    }
}
