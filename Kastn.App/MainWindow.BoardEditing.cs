using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    private bool boardEditing;

    private Task EditBoardSlipAsync(ZetlSlipSnapshot slip) => EditBoardSlipAsync(slip,
        (target, buckets) => KastnDialogs.EditSlipDialogAsync(this, target, buckets));

    private async Task EditBoardSlipAsync(ZetlSlipSnapshot slip,
        Func<ZetlSlipSnapshot, IReadOnlyList<ZetlBucketSnapshot>, Task<EditSlipResult?>> editSlip)
    {
        if (boardEditing || currentProject is null || !IsOnline
            || saving && inflightSave is not { IsCompleted: false })
            return;

        var context = new KastnEditorWorkflowContext(currentProject.Id, editorState);
        var ownsBusy = false;
        boardEditing = true;
        try
        {
            // Focus loss may already be saving the editor. Join it, and only
            // open the dialog once its baseline is settled and its draft clean.
            if (!await SaveEditorAsync() || !context.IsSameSession(currentProject?.Id, editorState)
                || editorState.IsDirty || editorState.ConflictCurrent is not null || !IsOnline || saving)
                return;

            // IPC refreshes publish off-thread. The prerequisite save's response
            // can reach us before its posted UI snapshot; settle that projection
            // before showing saved content in the dialog.
            if (slip.Id == editorState.SlipId
                && ProjectIndex.Slip(slip.Id) is { } displayed && displayed.Revision < editorState.Revision)
            {
                var latest = connection.Current;
                if (latest.Project?.Id != context.ProjectId
                    || latest.Project.Slips.FirstOrDefault(item => item.Id == slip.Id) is not { } savedSlip
                    || savedSlip.Revision < editorState.Revision)
                    return;
                ApplySnapshot(latest);
                if (!context.IsSameSession(currentProject?.Id, editorState)) return;
            }

            // A reused card may still close over an older snapshot.
            if (ProjectIndex.Slip(slip.Id) is not { } target || IsSlipInDeleted(target))
                return;
            context = new KastnEditorWorkflowContext(context.ProjectId, editorState);
            var result = await editSlip(target, currentProject!.Buckets);
            if (result is null || !result.Delete && !result.Save
                || !CanApplyBoardEdit(context, target))
                return;
            if (!result.Delete && result.DestinationBucketId is { } destination
                && (ProjectIndex.Bucket(destination) is not { } bucket || KastnWorkbench.IsDeletedBucket(bucket)))
                return;

            var operation = new KastnBoardEditOperation(context.ProjectId, target, result,
                currentProject!.Buckets.FirstOrDefault(KastnWorkbench.IsDeletedBucket)?.Id);
            ownsBusy = true;
            saving = true;
            SetEditingEnabled();
            using var undoGesture = BeginGesture(operation.IsDelete ? "Delete slip" : "Edit slip");
            await using var refreshBatch = connection.DeferRefresh();
            while (!operation.IsComplete)
            {
                var mutation = new KastnEditorMutationAcceptance(editorState,
                    operation.Command, operation.Payload);
                pendingEditorMutation = mutation;
                try
                {
                    var response = await ExecuteMutationAsync(mutation.Command);
                    if (!operation.TryAcceptResponse(response, out _))
                    {
                        if (context.IsSameSession(currentProject?.Id, editorState))
                        {
                            if (mutation.ReconcileConflict(response)) ShowConflict();
                            statusText.Text = response.Error?.Message ?? "The board edit could not be confirmed.";
                        }
                        return;
                    }

                    // Accept already-sent changes only into the original editor
                    // session. The acceptance object keeps newer typing/styles.
                    if (context.IsSameSession(currentProject?.Id, editorState)
                        && mutation.TryAcceptResponse(response) && mutation.IsCurrentEditor)
                    {
                        PersistEditorAfterSave(context.ProjectId);
                        UpdateEditorFromState();
                    }
                }
                finally
                {
                    pendingEditorMutation = null;
                }

                if (!operation.IsComplete && (!IsOnline
                    || !context.IsSameDraft(currentProject?.Id, editorState)))
                {
                    if (context.IsSameSession(currentProject?.Id, editorState))
                        statusText.Text = editorState.IsDirty
                            ? "Unsaved changes — slip moved; dialog changes were not saved."
                            : "Slip moved; dialog changes were not saved.";
                    return;
                }
            }

            if (!context.IsSameSession(currentProject?.Id, editorState)) return;
            // Delete is a move to Deleted. Retain writing entered while it was
            // in flight; only clear the editor that still has the original draft.
            if (operation.IsDelete && target.Id == editorState.SlipId
                && context.IsSameDraft(currentProject?.Id, editorState) && !editorState.IsDirty)
            {
                editorState.Select(null);
                UpdateEditorFromState();
                context = new KastnEditorWorkflowContext(context.ProjectId, editorState);
            }
            await connection.SynchronizeAsync();
            if (context.IsSameSession(currentProject?.Id, editorState))
                statusText.Text = editorState.ConflictCurrent is not null
                    ? "Resolve the slip conflict before continuing."
                    : editorState.IsDirty ? "Unsaved changes."
                        : operation.IsDelete ? "Slip moved to Deleted." : "Slip saved.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (context.IsSameSession(currentProject?.Id, editorState)) statusText.Text = ex.Message;
        }
        finally
        {
            if (ownsBusy) saving = false;
            boardEditing = false;
            SetEditingEnabled();
        }
    }

    private bool CanApplyBoardEdit(KastnEditorWorkflowContext context, ZetlSlipSnapshot target) =>
        IsOnline && !saving && context.IsSameDraft(currentProject?.Id, editorState)
        && ProjectIndex.Slip(target.Id) is { } current && current.Revision == target.Revision
        && current.BucketId == target.BucketId && !IsSlipInDeleted(current);
}
