using Avalonia.Controls;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    private async Task MoveSlipAsync()
    {
        if (!IsOnline || currentProject is null || moveBucketBox.SelectedItem is not KastnBucketItem { Id: { } destinationId } destination)
            return;
        var context = CaptureMutationContext();
        var ids = SelectedSlips().Where(s => !IsSlipInDeleted(s)).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        using var preparation = mutations.TryPrepare(KastnMutationPreparation.Move);
        if (preparation is null || ids.Count == 0) return;
        if (!await SaveEditorAsync() || !IsCurrentMutation(context) || editorState.IsDirty
            || !TrySettleSavedEditorSnapshot(context.ProjectId) || !IsCurrentMutation(context)
            || ProjectIndex.Bucket(destinationId) is not { } bucket || KastnWorkbench.IsDeletedBucket(bucket)) return;
        var targets = currentProject!.Slips.Where(s => ids.Contains(s.Id) && !IsSlipInDeleted(s)).ToArray();
        if (targets.Length == 0) return;
        using var busy = mutations.TryBeginWrite();
        if (busy is null) return;
        using var gesture = BeginGesture(targets.Length == 1 ? "Move slip" : "Move slips");
        await using var refresh = connection.DeferRefresh();
        try
        {
            var result = await new KastnSlipMutationBatch(context.ProjectId, targets).ExecuteAsync(
                (slip, _) => slip.BucketId == destinationId ? null : ZetlCommandEnvelope.Create(
                    Guid.NewGuid().ToString("N"), ZetlCommandKind.MoveSlip,
                    new MoveSlipCommand { DestinationBucketId = destinationId }, context.ProjectId, slip.Id, slip.Revision),
                command => ExecuteSlipBatchCommandAsync(command, context), () => IsCurrentMutation(context));
            if (result.Interrupted || !IsCurrentMutation(context)) return;
            navigation.RequestBucket(destinationId);
            await connection.SynchronizeAsync();
            if (!IsCurrentMutationScope(context)) return;
            if (!editorState.IsDirty)
            {
                if (targets.Length == 1) ReselectSlipNode(targets[0].Id);
                else { editorState.Select(null); UpdateEditorFromState(); }
            }
            var message = editorState.IsDirty ? "Unsaved changes — slip moved; newer edits remain unsaved."
                : BatchStatus(result.Changed > 0 ? $"{result.Changed} slip{Plural(result.Changed)} moved to {destination.Bucket?.Name}" : null,
                    result.Skipped > 0 ? $"{result.Skipped} already there" : null,
                    result.Failed > 0 ? $"{result.Failed} failed" : null);
            // Forced reselection is our own UI effect, not a competing request.
            if (targets.Length == 1 && !editorState.IsDirty) context = CaptureMutationContext();
            ShowMutationCompletion(context, message);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { if (IsCurrentMutation(context)) statusText.Text = ex.Message; }
    }

    private async Task RestoreSlipAsync()
    {
        if (!IsOnline || currentProject is null || SelectedSlip is not { } selected || !IsSlipInDeleted(selected)) return;
        var context = CaptureMutationContext();
        var destination = moveBucketBox.SelectedItem as KastnBucketItem ?? moveBuckets.FirstOrDefault();
        if (destination?.Id is not { } destinationId)
        { statusText.Text = "Create a regular bucket before restoring this slip."; return; }
        using var preparation = mutations.TryPrepare(KastnMutationPreparation.Restore);
        if (preparation is null || !await SaveEditorAsync() || !IsCurrentMutation(context) || editorState.IsDirty
            || !TrySettleSavedEditorSnapshot(context.ProjectId) || !IsCurrentMutation(context)
            || ProjectIndex.Slip(selected.Id) is not { } slip || !IsSlipInDeleted(slip)
            || ProjectIndex.Bucket(destinationId) is not { } bucket || KastnWorkbench.IsDeletedBucket(bucket)) return;
        using var busy = mutations.TryBeginWrite();
        if (busy is null) return;
        try
        {
            var response = await ExecuteSlipBatchCommandAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"), ZetlCommandKind.MoveSlip,
                new MoveSlipCommand { DestinationBucketId = destinationId }, context.ProjectId, slip.Id, slip.Revision), context);
            if (!IsCurrentMutation(context)) return;
            if (response.Status == ZetlResponseStatus.Success)
            {
                navigation.RequestBucket(destinationId);
                if (!editorState.IsDirty) navigation.RequestSlip(slip.Id);
                await connection.SynchronizeAsync();
            }
            if (IsCurrentMutation(context)) HandleSimpleResponse(response, $"Slip restored to {destination.Bucket?.Name}.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { if (IsCurrentMutation(context)) statusText.Text = ex.Message; }
    }

    private Task DeleteSlipAsync() => DeleteSlipAsync(count => KastnDialogs.ConfirmAsync(this,
        count == 1 ? "Move the selected slip to Deleted?" : $"Move {count} selected slips to Deleted?", "Delete Slip"));

    private async Task DeleteSlipAsync(Func<int, Task<bool>> confirm)
    {
        if (!IsOnline || currentProject is null) return;
        var context = CaptureMutationContext();
        var ids = SelectedSlips().Where(s => !IsSlipInDeleted(s)).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        using var preparation = mutations.TryPrepare(KastnMutationPreparation.Delete);
        if (preparation is null || ids.Count == 0 || !await SaveEditorAsync()
            || !IsCurrentMutation(context) || editorState.IsDirty
            || !TrySettleSavedEditorSnapshot(context.ProjectId) || !IsCurrentMutation(context)) return;
        var targets = currentProject!.Slips.Where(s => ids.Contains(s.Id) && !IsSlipInDeleted(s)).ToArray();
        if (targets.Length == 0 || !await confirm(targets.Length) || !IsCurrentMutation(context)
            || editorState.IsDirty || editorState.ConflictCurrent is not null) return;
        // A dialog answer may outlive an edit/removal of any captured target.
        if (targets.Any(s => ProjectIndex.Slip(s.Id) is not { } now || now.Revision != s.Revision || now.BucketId != s.BucketId)) return;
        using var busy = mutations.TryBeginWrite();
        if (busy is null) return;
        using var gesture = BeginGesture(targets.Length == 1 ? "Delete slip" : "Delete slips");
        await using var refresh = connection.DeferRefresh();
        try
        {
            var result = await new KastnSlipMutationBatch(context.ProjectId, targets).ExecuteAsync(
                (slip, _) => ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), ZetlCommandKind.DeleteSlip,
                    new DeleteSlipCommand(), context.ProjectId, slip.Id, slip.Revision),
                command => ExecuteSlipBatchCommandAsync(command, context), () => IsCurrentMutation(context),
                saved => { if (navigation.PendingBucketId is null) navigation.RequestBucket(saved.BucketId); });
            if (result.Interrupted || !IsCurrentMutation(context)) return;
            if (!editorState.IsDirty && editorState.ConflictCurrent is null && result.Changed > 0)
            {
                editorState.Select(null);
                ResetSlipFilters();
                UpdateEditorFromState();
                context = CaptureMutationContext();
            }
            await connection.SynchronizeAsync();
            if (!IsCurrentMutationScope(context)) return;
            ShowMutationCompletion(context, editorState.IsDirty ? "Unsaved changes — newer edits remain unsaved."
                : result.Failed == 0 ? $"{result.Changed} slip{Plural(result.Changed)} moved to Deleted."
                    : $"{result.Changed} slip{Plural(result.Changed)} moved to Deleted; {result.Failed} failed.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { if (IsCurrentMutation(context)) statusText.Text = ex.Message; }
    }
}
