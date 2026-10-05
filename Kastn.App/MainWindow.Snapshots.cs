using Avalonia.Threading;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    private readonly KastnSnapshotCoordinator snapshots;

    private KastnSnapshotCoordinator CreateSnapshotCoordinator() => new(editorState,
        () => connection?.NavigationVersion ?? 0, action => Dispatcher.UIThread.Post(action), new(
            EnterUiUpdate, ObserveSnapshotSession,
            () => new(SelectedBucketId, navigation.PendingSlipId ?? editorState.SlipId,
                (viewPickerBox.SelectedItem as ZetlViewDocument)?.Id),
            ResetSnapshotRendering, UpdateSnapshotProjects, AdoptSnapshotProject, UpdateSnapshotProjection,
            (project, slip) => pendingEditorSave is { } save && save.ProjectId == project.Id && save.TryAcknowledgeSnapshot(slip)
                || pendingEditorMutation is { } mutation && mutation.Command.ProjectId == project.Id
                    && mutation.TryAcknowledgeSnapshot(slip, project),
            UpdateEditorFromState, BindSlipEditor, RestoreDraftIfAvailable, PresentSnapshot, FinishSnapshot));

    private void OnSnapshotChanged(object? sender, KastnSessionSnapshot snapshot) => snapshots.Queue(snapshot);
    private void ApplySnapshot(KastnSessionSnapshot snapshot) => snapshots.ApplyNow(snapshot);

    private IDisposable EnterUiUpdate()
    {
        var previous = refreshing;
        refreshing = true;
        return new UiUpdateScope(() => refreshing = previous);
    }

    private void ObserveSnapshotSession(KastnSnapshotPlan plan)
    {
        // History may have observed a transport session before its UI delivery.
        // Coalesced scope round-trips must retire the original session as well.
        if (plan.ResetRendering) editHistory.Retire();
        editHistory.ObserveSession(plan.Snapshot);
        navigation.ObserveSession(plan.Snapshot.Project?.Id, editHistory.Generation);
    }

    private void ResetSnapshotRendering()
    {
        landing.ResetProjectActions();
        projectIndex = null;
        readerPresenter.Clear();
        boardPresenter.Clear();
        inspectorPresenter.Clear();
        viewRenderCache.Clear();
        pictureCache.Reset();
    }

    private void UpdateSnapshotProjects(KastnSessionSnapshot snapshot)
    {
        landing.UpdateProjects(snapshot.Projects);
        RefreshLandingGridLayout();
        ClearLandingSelection();
    }

    private KastnSnapshotSelection AdoptSnapshotProject(KastnSnapshotPlan plan, KastnSnapshotSelection selection)
    {
        currentProject = plan.Snapshot.Project;
        if (currentProject is { } project)
        {
            RefreshViewCatalog(project, plan.ProjectChanged ? project.DefaultViewId : selection.ViewId, force: plan.ServerChanged);
            if (plan.ProjectChanged)
            {
                stateStore.LastProjectId = project.Id;
                editorState.Select(null);
                // Tree identities and anchors are project-local, even if another
                // project happens to reuse the same IDs.
                treeProjection.Clear();
                projectTree.SetHierarchy(treeProjection.Roots);
                projectTree.SelectedItems?.Clear();
                projectTree.SelectedItem = null;
                var recoveryId = RecoverySlipId(project);
                if (recoveryId is not null) navigation.RequestSlip(recoveryId);
                selection = new(null, recoveryId, project.DefaultViewId);
                searchBox.Text = "";
                SelectViewForProject(project);
            }
            projectTitle.Text = project.Name;
            projectSummary.Text = $"{project.Buckets.Count} buckets, {project.Slips.Count} slips, change {project.ChangeSequence}";
            projectView.IsVisible = true;
            emptyState.IsVisible = false;
        }
        else
        {
            projectIndex = null;
            RefreshViewCatalog(null, force: false);
            treeProjection.Clear();
            projectTree.SetHierarchy(treeProjection.Roots);
            editorState.Select(null);
            UpdateEditorFromState();
            projectView.IsVisible = false;
            emptyState.IsVisible = true;
            landingModeToggle.IsVisible = plan.Snapshot.ConnectionState == KastnConnectionState.Online
                && (plan.Snapshot.Projects.Count > 0 || KastnTemplateCatalog.BuiltIns.Count > 0);
            emptyStateText.Text = plan.Snapshot.ConnectionState == KastnConnectionState.Online
                ? "Select a project or template to begin." : plan.Snapshot.Status;
            RefreshLandingMode();
        }
        return selection;
    }

    private void UpdateSnapshotProjection(KastnSnapshotPlan plan, KastnSnapshotSelection selection)
    {
        var project = plan.Snapshot.Project!;
        RefreshFilterChoices(project);
        RefreshBuckets(project, selection.BucketId, refreshDetails: false);
    }

    private void PresentSnapshot()
    {
        RefreshBucketEditor();
        RefreshDestinationBuckets();
        SetDetailPaneMode(detailShowingMetadata, refreshInspector: false);
        RefreshViewer();
    }

    private void FinishSnapshot(KastnSessionSnapshot snapshot)
    {
        // Local authoring drafts remain open while their project context is
        // reconciled. Structured view writes keep their original session guards.
        if (templateEditor.State is not null)
        {
            projectView.IsVisible = false;
            emptyState.IsVisible = false;
            viewEditorView.IsVisible = false;
            templateEditorView.IsVisible = true;
        }
        else if (viewEditor.State is not null)
        {
            projectView.IsVisible = false;
            emptyState.IsVisible = false;
            templateEditorView.IsVisible = false;
            viewEditorView.IsVisible = true;
        }
        else if (creationEditor.State is not null)
        {
            projectView.IsVisible = false;
            emptyState.IsVisible = false;
            templateEditorView.IsVisible = false;
            viewEditorView.IsVisible = false;
            creationEditorView.IsVisible = true;
        }
        viewEditor.RefreshPreview();
        SetConnectionState(snapshot);
    }

    private sealed class UiUpdateScope(Action release) : IDisposable
    {
        private Action? current = release;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Invoke();
    }
}
