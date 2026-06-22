using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

// Side-pane drag-and-drop (UI roadmap item 11): drag a slip onto a bucket to move
// it, onto another slip to reorder/move-before it, or drag a bucket onto another
// bucket to re-parent it (onto empty space to promote it to top level). Every drop
// is one of the existing sole-writer commands (MoveSlip / ReorderSlip / UpdateBucket)
// — no new persisted state, and the protected Deleted bucket is never dragged or hit.
internal partial class MainWindow
{
    private const string DragNodeFormat = "application/x-kastn-tree-node";
    private const double DragThreshold = 4;

    private KastnTreeNode? dragCandidate;
    private KastnTreeNode? draggingNode;
    private Point dragStart;
    private bool dragInProgress;

    private void SetupTreeDragDrop()
    {
        projectTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        projectTree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(projectTree, true);
        projectTree.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
        projectTree.AddHandler(DragDrop.DropEvent, OnTreeDrop);
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs args)
    {
        dragCandidate = null;
        if (!args.GetCurrentPoint(projectTree).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var node = NodeFromVisual(args.Source as Visual);
        if (node is null || !CanDragNode(node))
        {
            return;
        }

        dragCandidate = node;
        dragStart = args.GetPosition(projectTree);
    }

    private async void OnTreePointerMoved(object? sender, PointerEventArgs args)
    {
        if (dragCandidate is null || dragInProgress)
        {
            return;
        }

        if (!args.GetCurrentPoint(projectTree).Properties.IsLeftButtonPressed)
        {
            dragCandidate = null;
            return;
        }

        var position = args.GetPosition(projectTree);
        if (Math.Abs(position.X - dragStart.X) < DragThreshold
            && Math.Abs(position.Y - dragStart.Y) < DragThreshold)
        {
            return;
        }

        var node = dragCandidate;
        dragCandidate = null;
        draggingNode = node;
        dragInProgress = true;
        try
        {
            var data = new DataObject();
            data.Set(DragNodeFormat, node.Id);
            await DragDrop.DoDragDrop(args, data, DragDropEffects.Move);
        }
        finally
        {
            dragInProgress = false;
            draggingNode = null;
        }
    }

    private void OnTreeDragOver(object? sender, DragEventArgs args)
    {
        args.DragEffects = PlanDrop(args) is not null
            ? DragDropEffects.Move
            : DragDropEffects.None;
        args.Handled = true;
    }

    private async void OnTreeDrop(object? sender, DragEventArgs args)
    {
        if (PlanDrop(args) is not { } plan)
        {
            return;
        }

        args.Handled = true;
        await ApplyDropAsync(plan);
    }

    private enum DropAction
    {
        SlipMove,
        BucketReparent
    }

    private readonly record struct DropPlan(
        DropAction Action,
        KastnTreeNode Source,
        string? DestinationBucketId,
        string? BeforeSlipId,
        string? NewParentBucketId);

    // Resolve the dragged node + the node under the pointer into a concrete plan, or
    // null when the drop is invalid (no-op, onto itself, into Deleted, or a cycle).
    private DropPlan? PlanDrop(DragEventArgs args)
    {
        if (draggingNode is not { } source
            || currentProject is not { } project
            || !IsOnline
            || !args.Data.Contains(DragNodeFormat))
        {
            return null;
        }

        var target = NodeFromVisual(args.Source as Visual);

        if (source.Kind == KastnTreeNodeKind.Slip)
        {
            if (source.Slip is not { } slip || IsSlipInDeleted(slip) || target is null)
            {
                return null;
            }

            if (target.Kind == KastnTreeNodeKind.Bucket)
            {
                if (target.Bucket is not { } bucket || KastnWorkbench.IsDeletedBucket(bucket))
                {
                    return null;
                }

                // Drop onto a bucket appends to its end; a no-op if already last there.
                return slip.BucketId == bucket.Id
                    ? null
                    : new DropPlan(DropAction.SlipMove, source, bucket.Id, null, null);
            }

            if (target.Slip is not { } targetSlip
                || IsSlipInDeleted(targetSlip)
                || targetSlip.Id == slip.Id)
            {
                return null;
            }

            // Drop onto a slip places the dragged slip immediately before it.
            return new DropPlan(DropAction.SlipMove, source, targetSlip.BucketId, targetSlip.Id, null);
        }

        // Dragging a bucket.
        if (source.Bucket is not { } moving || KastnWorkbench.IsDeletedBucket(moving))
        {
            return null;
        }

        if (target is null)
        {
            // Dropped on empty space → promote to top level (no-op if already there).
            return moving.ParentBucketId is null
                ? null
                : new DropPlan(DropAction.BucketReparent, source, null, null, null);
        }

        // Onto a bucket re-parents under it; onto a slip re-parents under the slip's bucket.
        var targetBucket = target.Kind == KastnTreeNodeKind.Bucket
            ? target.Bucket
            : project.Buckets.FirstOrDefault(bucket => bucket.Id == target.Slip?.BucketId);
        if (targetBucket is null
            || KastnWorkbench.IsDeletedBucket(targetBucket)
            || targetBucket.Id == moving.Id
            || moving.ParentBucketId == targetBucket.Id
            || IsDescendant(project, targetBucket.Id, moving.Id))
        {
            return null;
        }

        return new DropPlan(DropAction.BucketReparent, source, null, null, targetBucket.Id);
    }

    private async Task ApplyDropAsync(DropPlan plan)
    {
        if (!await SaveEditorAsync())
        {
            statusText.Text = "Save or resolve the current slip before moving things.";
            return;
        }

        try
        {
            if (plan.Action == DropAction.SlipMove)
            {
                await ApplySlipDropAsync(plan);
            }
            else
            {
                await ApplyBucketReparentAsync(plan);
            }
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            statusText.Text = ex.Message;
        }
    }

    private async Task ApplySlipDropAsync(DropPlan plan)
    {
        if (currentProject is not { } project
            || project.Slips.FirstOrDefault(slip => slip.Id == plan.Source.Id) is not { } slip)
        {
            return;
        }

        var revision = string.Equals(slip.Id, editorState.SlipId, StringComparison.Ordinal)
            ? editorState.Revision
            : slip.Revision;
        pendingBucketSelectionId = plan.DestinationBucketId;

        if (slip.BucketId != plan.DestinationBucketId)
        {
            var moveResponse = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.MoveSlip,
                new MoveSlipCommand { DestinationBucketId = plan.DestinationBucketId! },
                project.Id,
                slip.Id,
                revision));
            if (moveResponse.Status != ZetlResponseStatus.Success)
            {
                statusText.Text = moveResponse.Error?.Message ?? $"Move failed: {moveResponse.Status}.";
                return;
            }

            revision = moveResponse.Payload?.Deserialize<ZetlSlipSnapshot>(
                ZetlProtocolJson.Options)?.Revision ?? revision;
        }

        var statusMessage = "Slip moved.";
        if (plan.BeforeSlipId is { } beforeId && !string.Equals(beforeId, slip.Id, StringComparison.Ordinal))
        {
            var reorderResponse = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.ReorderSlip,
                new ReorderSlipCommand { BeforeSlipId = beforeId },
                project.Id,
                slip.Id,
                revision));
            statusMessage = reorderResponse.Status == ZetlResponseStatus.Success
                ? "Slip moved."
                : reorderResponse.Error?.Message ?? $"Reorder failed: {reorderResponse.Status}.";
        }

        await connection.RefreshAsync();
        // Re-drive selection through the tree (not the snapshot/pending path): the tree
        // node is restored under the refresh guard, which never syncs the editor, so the
        // moved slip would otherwise show stale editor content. Selecting it here runs the
        // canonical OnTreeSelectionChanged path and syncs editor, inspector, and View.
        ReselectSlipNode(slip.Id);
        statusText.Text = statusMessage;
    }

    private void ReselectSlipNode(string slipId)
    {
        var node = FindTreeNode(projectTree.ItemsSource as IEnumerable<KastnTreeNode>, slipId);
        if (node is null)
        {
            return;
        }

        // Clearing first guarantees the assignment is a change even when the refresh
        // already restored this node, so OnTreeSelectionChanged fires and re-syncs.
        projectTree.SelectedItem = null;
        projectTree.SelectedItem = node;
    }

    private async Task ApplyBucketReparentAsync(DropPlan plan)
    {
        if (currentProject is not { } project
            || plan.Source.Bucket is not { } bucket)
        {
            return;
        }

        pendingBucketSelectionId = bucket.Id;
        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.UpdateBucket,
            new UpdateBucketCommand
            {
                Name = bucket.Name,
                ParentBucketId = plan.NewParentBucketId,
                Settings = bucket.Settings
            },
            project.Id,
            bucket.Id,
            bucket.Revision));
        if (response.Status == ZetlResponseStatus.Success)
        {
            await connection.RefreshAsync();
        }

        HandleSimpleResponse(response, "Bucket moved.");
    }

    private bool CanDragNode(KastnTreeNode node)
    {
        return node.Kind == KastnTreeNodeKind.Slip
            ? node.Slip is { } slip && !IsSlipInDeleted(slip)
            : node.Bucket is { } bucket && !KastnWorkbench.IsDeletedBucket(bucket);
    }

    // Walk up from the event source to the tree row whose DataContext is a node.
    private static KastnTreeNode? NodeFromVisual(Visual? visual)
    {
        while (visual is not null)
        {
            if (visual is StyledElement { DataContext: KastnTreeNode node })
            {
                return node;
            }

            visual = visual.GetVisualParent();
        }

        return null;
    }
}
