using ZETL.Contracts;

namespace KASTN;

internal sealed record KastnSnapshotSelection(string? BucketId, string? SlipId, string? ViewId);
internal sealed record KastnSnapshotPlan(KastnSessionSnapshot Snapshot, bool ProjectChanged, bool ServerChanged,
    bool ProjectScopeChanged)
{
    public bool ResetRendering => ProjectScopeChanged || ServerChanged;
}
internal sealed record KastnSnapshotEffects(
    Func<IDisposable> EnterUpdate,
    Action<KastnSnapshotPlan> ObserveSession,
    Func<KastnSnapshotSelection> CaptureSelection,
    Action ResetRendering,
    Action<KastnSessionSnapshot> UpdateProjects,
    Func<KastnSnapshotPlan, KastnSnapshotSelection, KastnSnapshotSelection> AdoptProject,
    Action<KastnSnapshotPlan, KastnSnapshotSelection> UpdateProjection,
    Func<ZetlProjectSnapshot, ZetlSlipSnapshot?, bool> TryAcknowledgeEditor,
    Action UpdateEditor,
    Action BindEditor,
    Action<ZetlProjectSnapshot> RestoreDraft,
    Action Present,
    Action<KastnSessionSnapshot> UpdateConnection);

// Owns delivery admission, project/server lifetimes and the reconciliation order.
// Queued bursts render their latest projection, but every admitted scope change
// advances an epoch: A -> B -> A still retires A's old controls and callbacks.
// Synchronous workflow application supersedes older queued deliveries. Native
// effects and the dispatcher are injected; no controls live in this owner.
// Queue is transport-thread safe; ApplyNow, Drain and native effects run on the UI thread.
internal sealed class KastnSnapshotCoordinator(KastnEditorState editor, Func<long> navigationVersion,
    Action<Action> dispatch, KastnSnapshotEffects effects)
{
    private sealed record Delivery(KastnSessionSnapshot Snapshot, long Ticket, long ProjectEpoch, long ServerEpoch);
    private readonly object gate = new();
    private Delivery? pending;
    private bool posted;
    private bool applying;
    private volatile bool retired;
    private long ticket;
    private long publication;
    private long observedNavigation;
    private string? observedProject;
    private string? observedServer;
    private long projectEpoch;
    private long serverEpoch;
    private long appliedProjectEpoch;
    private long appliedServerEpoch;
    private string? appliedProject;

    public void Queue(KastnSessionSnapshot snapshot)
    {
        var post = false;
        lock (gate)
        {
            if (Admit(snapshot) is not { } delivery) return;
            pending = delivery;
            if (!posted) { posted = true; post = true; }
        }
        if (post) dispatch(Drain);
    }

    public void ApplyNow(KastnSessionSnapshot snapshot)
    {
        Delivery delivery;
        lock (gate)
        {
            if (Admit(snapshot) is not { } admitted) return;
            delivery = admitted;
            if (applying) { pending = delivery; return; }
            pending = null;
        }
        Apply(delivery);
    }

    private Delivery? Admit(KastnSessionSnapshot snapshot)
    {
        if (retired || snapshot.PublicationVersion > 0
            && (snapshot.PublicationVersion < publication || snapshot.NavigationVersion != navigationVersion())) return null;
        publication = Math.Max(publication, snapshot.PublicationVersion);
        var navigation = snapshot.PublicationVersion > 0 ? snapshot.NavigationVersion : navigationVersion();
        if (observedProject != snapshot.Project?.Id || observedNavigation != navigation) projectEpoch++;
        observedProject = snapshot.Project?.Id;
        observedNavigation = navigation;
        if (snapshot.ConnectionState == KastnConnectionState.Online && snapshot.ServerInstanceId is { } server)
        {
            if (observedServer is not null && observedServer != server) serverEpoch++;
            observedServer = server;
        }
        return new(snapshot, ++ticket, projectEpoch, serverEpoch);
    }

    private void Drain()
    {
        Delivery? delivery;
        lock (gate)
        {
            posted = false;
            // Nested dispatcher processing must not enter native reconciliation
            // again. The outer application schedules the retained delivery.
            if (applying) return;
            delivery = pending;
            pending = null;
        }
        if (delivery is not null) Apply(delivery);
    }

    private void Apply(Delivery delivery)
    {
        lock (gate)
        {
            if (retired || delivery.Ticket != ticket || delivery.Snapshot.PublicationVersion > 0
                && delivery.Snapshot.NavigationVersion != navigationVersion()) return;
        }
        var plan = new KastnSnapshotPlan(delivery.Snapshot, delivery.Snapshot.Project?.Id != appliedProject,
            delivery.ServerEpoch != appliedServerEpoch, delivery.ProjectEpoch != appliedProjectEpoch);
        applying = true;
        try
        {
            using var update = effects.EnterUpdate();
            effects.ObserveSession(plan);
            var selection = effects.CaptureSelection();
            if (plan.ResetRendering) effects.ResetRendering();
            effects.UpdateProjects(plan.Snapshot);
            selection = effects.AdoptProject(plan, selection);
            if (plan.Snapshot.Project is { } project)
            {
                effects.UpdateProjection(plan, selection);
                var slip = project.Slips.FirstOrDefault(s => s.Id == selection.SlipId);
                if (plan.ServerChanged || plan.ProjectScopeChanged && !plan.ProjectChanged) editor.ReconcileSession(slip);
                else if (!effects.TryAcknowledgeEditor(project, slip)) editor.Reconcile(slip);
                effects.UpdateEditor();
                effects.BindEditor();
                effects.RestoreDraft(project);
            }
            if (retired) return;
            effects.Present();
            effects.UpdateConnection(plan.Snapshot);
            appliedProjectEpoch = delivery.ProjectEpoch;
            appliedServerEpoch = delivery.ServerEpoch;
            appliedProject = delivery.Snapshot.Project?.Id;
        }
        finally
        {
            applying = false;
            var post = false;
            lock (gate)
            {
                if (!retired && pending is not null && !posted) { posted = true; post = true; }
            }
            if (post) dispatch(Drain);
        }
    }

    public void Retire() { lock (gate) { retired = true; pending = null; } }
}
