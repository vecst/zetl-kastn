using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
    private static readonly DataFormat<string> DragNodeFormat =
        DataFormat.CreateStringApplicationFormat("kastn-tree-node");
    private const double DragThreshold = 4;

    private KastnTreeNode? dragCandidate;
    // The selection captured at pointer-press (tunnel), before the TreeView collapses a
    // multi-selection to the single pressed item — the basis for multi-slip drag.
    private IReadOnlyList<KastnTreeNode> pressSelection = [];
    private KastnTreeNode? draggingNode;
    // The full set being dragged (multiple slips when a multi-selection is grabbed);
    // the primary draggingNode resolves the drop target, this set is what moves.
    private IReadOnlyList<KastnTreeNode> draggingNodes = [];
    private Point dragStart;
    private bool dragInProgress;

    // Edge auto-scroll while dragging: the tree scrolls when the pointer is held near
    // its top/bottom edge, so a drag from the bottom can still reach items above.
    private const double DragScrollEdge = 30;
    private const double DragScrollStep = 20;
    private DispatcherTimer? dragScrollTimer;
    private double lastDragPointerY;
    private bool dragPointerInsideTree;

    // The row currently showing the drop marker.
    private KastnTreeNode? dropTargetNode;
    // When a press lands on an item that is part of a multi-selection, the collapse to
    // that single item is deferred to pointer-release — so a drag in between keeps the
    // whole selection (and a plain click still narrows to the one item).
    private KastnTreeNode? deferredCollapseNode;

    private void SetupTreeDragDrop()
    {
        projectTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        projectTree.AddHandler(PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Tunnel);
        projectTree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(projectTree, true);
        projectTree.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
        projectTree.AddHandler(DragDrop.DragLeaveEvent, OnTreeDragLeave);
        projectTree.AddHandler(DragDrop.DropEvent, OnTreeDrop);
        dragScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        dragScrollTimer.Tick += OnDragScrollTick;

        DragDrop.SetAllowDrop(boardScrollViewer, true);
        boardScrollViewer.AddHandler(DragDrop.DragOverEvent, OnBoardDragOver);
        boardScrollViewer.AddHandler(DragDrop.DragLeaveEvent, OnBoardDragLeave);
        boardScrollViewer.AddHandler(DragDrop.DropEvent, OnBoardDrop);
        boardDragScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        boardDragScrollTimer.Tick += OnBoardDragScrollTick;
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs args)
    {
        dragCandidate = null;
        pressSelection = [];
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
        // Capture the live multi-selection now (tunnel phase): the TreeView is about to
        // collapse it to this one item, but a drag should still move the whole selection.
        pressSelection = projectTree.SelectedItems?.OfType<KastnTreeNode>().ToList() ?? [];
        dragStart = args.GetPosition(projectTree);

        // Pressing a member of a multi-selection (no Ctrl/Shift): keep the selection now
        // and defer the collapse to release, so a drag in between moves the whole set.
        var unmodified = (args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) == 0;
        if (unmodified && pressSelection.Count > 1 && pressSelection.Any(item => item.Id == node.Id))
        {
            deferredCollapseNode = node;
            args.Handled = true;
        }
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        // A press that deferred its collapse and did not turn into a drag was a plain
        // click — narrow the selection to that one item now.
        if (deferredCollapseNode is { } node && !dragInProgress)
        {
            projectTree.SelectedItem = node;
        }

        deferredCollapseNode = null;
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
        // A drag began, so the deferred collapse must not fire on the post-drag release.
        deferredCollapseNode = null;
        draggingNode = node;
        draggingNodes = ResolveDragSet(node);
        dragInProgress = true;
        dragPointerInsideTree = true;
        dragScrollTimer?.Start();
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(DragNodeFormat, node.Id));
            await DragDrop.DoDragDropAsync(args, data, DragDropEffects.Move);
        }
        finally
        {
            dragScrollTimer?.Stop();
            SetDropTarget(null);
            dragInProgress = false;
            dragPointerInsideTree = false;
            draggingNode = null;
            draggingNodes = [];
        }
    }

    // The nodes to move for this drag. Multi-drag applies only to slips: grabbing a slip
    // that is part of a multi-selection drags every selected (non-deleted) slip, in
    // document order; otherwise just the grabbed node moves.
    private IReadOnlyList<KastnTreeNode> ResolveDragSet(KastnTreeNode node)
    {
        if (node.Kind != KastnTreeNodeKind.Slip)
        {
            return [node];
        }

        if (pressSelection.Count <= 1 || pressSelection.All(item => item.Id != node.Id))
        {
            return [node];
        }

        var slipNodes = pressSelection
            .Where(item => item.Kind == KastnTreeNodeKind.Slip
                && item.Slip is { } slip && !IsSlipInDeleted(slip))
            .ToList();
        if (slipNodes.Count <= 1)
        {
            return [node];
        }

        var order = currentProject is { } project
            ? project.Slips.Select((slip, index) => (slip.Id, index))
                .ToDictionary(entry => entry.Id, entry => entry.index, StringComparer.Ordinal)
            : [];
        slipNodes.Sort((a, b) =>
            order.GetValueOrDefault(a.Id, int.MaxValue)
                .CompareTo(order.GetValueOrDefault(b.Id, int.MaxValue)));
        return slipNodes;
    }

    private void OnTreeDragOver(object? sender, DragEventArgs args)
    {
        // Remember where the pointer is so the auto-scroll timer can keep scrolling even
        // while the pointer is held still at the edge (DragOver only fires on movement).
        lastDragPointerY = args.GetPosition(projectTree).Y;
        dragPointerInsideTree = true;
        var plan = PlanDrop(args);
        args.DragEffects = plan is not null ? DragDropEffects.Move : DragDropEffects.None;
        // Mark the row that would receive a valid drop (the node under the pointer); a
        // bucket-promote onto empty space, or an invalid drop, marks nothing.
        var marksRow = plan is { } valid
            && (valid.DestinationBucketId is not null || valid.NewParentBucketId is not null);
        SetDropTarget(marksRow ? NodeFromVisual(args.Source as Visual) : null);
        args.Handled = true;
    }

    private void OnTreeDragLeave(object? sender, DragEventArgs args)
    {
        dragPointerInsideTree = false;
        SetDropTarget(null);
    }

    // Move the drop marker to a new row, clearing the previous one.
    private void SetDropTarget(KastnTreeNode? node)
    {
        if (ReferenceEquals(dropTargetNode, node))
        {
            return;
        }

        if (dropTargetNode is not null)
        {
            dropTargetNode.IsDropTarget = false;
        }

        dropTargetNode = node;
        if (dropTargetNode is not null)
        {
            dropTargetNode.IsDropTarget = true;
        }
    }

    private void OnDragScrollTick(object? sender, EventArgs args)
    {
        if (!dragInProgress || !dragPointerInsideTree || TreeScrollViewer() is not { } scroll)
        {
            return;
        }

        var height = projectTree.Bounds.Height;
        var maxY = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        if (lastDragPointerY < DragScrollEdge)
        {
            scroll.Offset = scroll.Offset.WithY(Math.Max(0, scroll.Offset.Y - DragScrollStep));
        }
        else if (lastDragPointerY > height - DragScrollEdge)
        {
            scroll.Offset = scroll.Offset.WithY(Math.Min(maxY, scroll.Offset.Y + DragScrollStep));
        }
    }

    private ScrollViewer? TreeScrollViewer() =>
        projectTree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

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
            || !args.DataTransfer.Contains(DragNodeFormat))
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
                var dragIds = draggingNodes.Count > 0
                    ? draggingNodes.Where(n => n.Kind == KastnTreeNodeKind.Slip).Select(n => n.Id).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal) { slip.Id };

                var bucketSlips = project.Slips.Where(s => s.BucketId == bucket.Id).ToList();
                var isNoOp = dragIds.Count > 0 && bucketSlips.Count >= dragIds.Count &&
                             bucketSlips.Skip(bucketSlips.Count - dragIds.Count).All(s => dragIds.Contains(s.Id));

                return isNoOp
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
        if (currentProject is not { } project)
        {
            return;
        }

        // The dragged slip ids (the multi-selection set, or just the primary), resolved to
        // current snapshots in document order. A slip dropped before itself is skipped.
        var draggedIds = (draggingNodes.Count > 0 ? draggingNodes : [plan.Source])
            .Where(node => node.Kind == KastnTreeNodeKind.Slip)
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);
        var slips = project.Slips
            .Where(slip => draggedIds.Contains(slip.Id)
                && !string.Equals(slip.Id, plan.BeforeSlipId, StringComparison.Ordinal))
            .ToList();
        if (slips.Count == 0)
        {
            return;
        }

        pendingBucketSelectionId = plan.DestinationBucketId;
        var moved = 0;
        string? failure = null;
        // Send every move/reorder as a batch: the service publishes a snapshot per
        // command, but dropping the intermediate pushes (one rebuild at the end) keeps a
        // multi-slip move snappy instead of shuffling the slips in one at a time.
        batching = true;
        try
        {
            // Iterating in document order and reordering each "before" the target lands
            // the set contiguously in its original order; moves to a bucket append too.
            foreach (var slip in slips)
            {
                var revision = string.Equals(slip.Id, editorState.SlipId, StringComparison.Ordinal)
                    ? editorState.Revision
                    : slip.Revision;

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
                        failure = moveResponse.Error?.Message ?? $"Move failed: {moveResponse.Status}.";
                        break;
                    }

                    revision = moveResponse.Payload?.Deserialize<ZetlSlipSnapshot>(
                        ZetlProtocolJson.Options)?.Revision ?? revision;
                }

                var isSameBucket = slip.BucketId == plan.DestinationBucketId;
                var needsEndReorder = isSameBucket && plan.BeforeSlipId is null &&
                                      (slips.Count > 1 || project.Slips.LastOrDefault(s => s.BucketId == plan.DestinationBucketId)?.Id != slip.Id);

                if ((plan.BeforeSlipId is { } beforeId && !string.Equals(beforeId, slip.Id, StringComparison.Ordinal))
                    || needsEndReorder)
                {
                    var reorderResponse = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                        Guid.NewGuid().ToString("N"),
                        ZetlCommandKind.ReorderSlip,
                        new ReorderSlipCommand { BeforeSlipId = plan.BeforeSlipId },
                        project.Id,
                        slip.Id,
                        revision));
                    if (reorderResponse.Status != ZetlResponseStatus.Success)
                    {
                        failure = reorderResponse.Error?.Message
                            ?? $"Reorder failed: {reorderResponse.Status}.";
                        break;
                    }
                }

                moved++;
            }
        }
        finally
        {
            batching = false;
        }

        await connection.RefreshAsync();
        // Re-drive selection through the tree (not the snapshot/pending path): the tree
        // node is restored under the refresh guard, which never syncs the editor, so the
        // moved slip would otherwise show stale editor content. Selecting it here runs the
        // canonical OnTreeSelectionChanged path and syncs editor, inspector, and View.
        ReselectSlipNode(plan.Source.Id);
        statusText.Text = failure
            ?? (moved == 1 ? "Slip moved." : $"{moved} slips moved.");
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

    private KastnTreeNode? boardDragCandidate;
    private Point boardDragStart;
    private bool boardDragInProgress;

    private void OnBoardCardPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        boardDragCandidate = null;
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var border = sender as Border;
        var node = border?.DataContext as KastnTreeNode;
        if (node is null || node.Kind != KastnTreeNodeKind.Slip || node.Slip is null || IsSlipInDeleted(node.Slip))
        {
            return;
        }

        boardDragCandidate = node;
        boardDragStart = args.GetPosition(this);
    }

    private void OnBoardCardPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        boardDragCandidate = null;
    }

    private async void OnBoardCardPointerMoved(object? sender, PointerEventArgs args)
    {
        if (boardDragCandidate is null || boardDragInProgress)
        {
            return;
        }

        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            boardDragCandidate = null;
            return;
        }

        var position = args.GetPosition(this);
        if (Math.Abs(position.X - boardDragStart.X) < DragThreshold
            && Math.Abs(position.Y - boardDragStart.Y) < DragThreshold)
        {
            return;
        }

        var node = boardDragCandidate;
        boardDragCandidate = null;

        draggingNode = node;
        draggingNodes = [node];
        boardDragInProgress = true;
        boardDragPointerInsideBoard = true;
        boardDragScrollTimer?.Start();
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(DragNodeFormat, node.Id));
            await DragDrop.DoDragDropAsync(args, data, DragDropEffects.Move);
        }
        finally
        {
            boardDragScrollTimer?.Stop();
            draggingNode = null;
            draggingNodes = [];
            boardDragInProgress = false;
            boardDragPointerInsideBoard = false;
        }
    }

    private void OnBoardDragOver(object? sender, DragEventArgs args)
    {
        lastBoardDragPointerX = args.GetPosition(boardScrollViewer).X;
        boardDragPointerInsideBoard = true;

        var plan = PlanDrop(args);
        args.DragEffects = plan is not null ? DragDropEffects.Move : DragDropEffects.None;
        args.Handled = true;
    }

    private void OnBoardDragLeave(object? sender, RoutedEventArgs args)
    {
        boardDragPointerInsideBoard = false;
        args.Handled = true;
    }

    private async void OnBoardDrop(object? sender, DragEventArgs args)
    {
        if (PlanDrop(args) is not { } plan)
        {
            return;
        }

        args.Handled = true;
        await ApplyDropAsync(plan);
    }

    private DispatcherTimer? boardDragScrollTimer;
    private double lastBoardDragPointerX;
    private bool boardDragPointerInsideBoard;

    private void OnBoardDragScrollTick(object? sender, EventArgs args)
    {
        if (!boardDragInProgress || !boardDragPointerInsideBoard || boardScrollViewer is not { } scroll)
        {
            return;
        }

        var width = scroll.Bounds.Width;
        var maxX = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
        const double edge = 40;
        const double step = 20;

        if (lastBoardDragPointerX < edge)
        {
            scroll.Offset = scroll.Offset.WithX(Math.Max(0, scroll.Offset.X - step));
        }
        else if (lastBoardDragPointerX > width - edge)
        {
            scroll.Offset = scroll.Offset.WithX(Math.Min(maxX, scroll.Offset.X + step));
        }
    }
}
