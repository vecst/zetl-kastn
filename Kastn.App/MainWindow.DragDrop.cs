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

    // The row currently showing a drop marker (highlight or insertion line).
    private KastnTreeNode? markerNode;

    // The last drop position resolved during the current drag. When the pointer slips into
    // a gap between rows (where nothing resolves), the drag holds this position instead of
    // rejecting the drop — so there is no dead band between adjacent slots. It only changes
    // when the pointer moves far enough to resolve a new slot.
    private DropPlan? stickyDropPlan;
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
        stickyDropPlan = null;
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
            ApplyDropMarker(null);
            stickyDropPlan = null;
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
        var resolved = PlanDrop(args);
        if (resolved is not null)
        {
            stickyDropPlan = resolved;
        }

        var plan = resolved ?? stickyDropPlan;
        args.DragEffects = plan is not null ? DragDropEffects.Move : DragDropEffects.None;
        ApplyDropMarker(plan);
        args.Handled = true;
    }

    private void OnTreeDragLeave(object? sender, DragEventArgs args)
    {
        dragPointerInsideTree = false;
        ApplyDropMarker(null);
    }

    // Show the plan's drag feedback on its marker row — a highlight to nest into, or an
    // insertion line to reorder before/after — clearing the previously marked row.
    private void ApplyDropMarker(DropPlan? plan)
    {
        var node = plan?.MarkerNode;
        var edge = plan?.MarkerEdge ?? KastnDropEdge.None;

        if (markerNode is not null && !ReferenceEquals(markerNode, node))
        {
            markerNode.IsDropTarget = false;
            markerNode.DropEdge = KastnDropEdge.None;
        }

        markerNode = node;
        if (node is not null)
        {
            node.IsDropTarget = edge == KastnDropEdge.None;
            node.DropEdge = edge;
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
        if ((PlanDrop(args) ?? stickyDropPlan) is not { } plan)
        {
            return;
        }

        args.Handled = true;
        await ApplyDropAsync(plan);
    }

    private enum DropAction
    {
        SlipMove,
        BucketReparent,
        BucketReorder
    }

    private readonly record struct DropPlan(
        DropAction Action,
        KastnTreeNode Source,
        string? DestinationBucketId,
        string? BeforeSlipId,
        string? NewParentBucketId)
    {
        // Bucket reorder: the sibling to land immediately before (null = end of the
        // sibling group under NewParentBucketId).
        public string? BeforeBucketId { get; init; }

        // The row that shows drag feedback. MarkerEdge None highlights it as a drop-into
        // target; Before/After draws an insertion line at that edge instead.
        public KastnTreeNode? MarkerNode { get; init; }
        public KastnDropEdge MarkerEdge { get; init; }
    }

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

        if (source.Kind == KastnTreeNodeKind.Slip)
        {
            return PlanSlipDrop(args, project, source);
        }

        // Dragging a bucket.
        if (source.Bucket is not { } moving || KastnWorkbench.IsDeletedBucket(moving))
        {
            return null;
        }

        // No row under the pointer (empty space below the list) → move to the end of the
        // top level. This is what makes "drop past the last bucket" reach the bottom.
        if (RowUnderPointer(args) is not { } row)
        {
            return BucketReorderPlan(project, source, moving, newParentId: null, beforeBucketId: null,
                markerNode: null, markerEdge: KastnDropEdge.None);
        }

        var (targetNode, fraction) = row;
        var targetBucket = targetNode.Kind == KastnTreeNodeKind.Bucket
            ? targetNode.Bucket
            : project.Buckets.FirstOrDefault(bucket => bucket.Id == targetNode.Slip?.BucketId);
        if (targetBucket is null || KastnWorkbench.IsDeletedBucket(targetBucket))
        {
            return null;
        }

        // Over a bucket row: the top edge inserts before it, the bottom edge after it, and
        // the middle nests into it (the existing reparent). A slip row only nests into its
        // bucket.
        if (targetNode.Kind == KastnTreeNodeKind.Bucket)
        {
            var edge = EdgeFromFraction(fraction, allowInto: true);
            if (edge == KastnDropEdge.Before)
            {
                return BucketReorderPlan(project, source, moving,
                    newParentId: targetBucket.ParentBucketId, beforeBucketId: targetBucket.Id,
                    markerNode: targetNode, markerEdge: KastnDropEdge.Before);
            }

            if (edge == KastnDropEdge.After)
            {
                return BucketReorderPlan(project, source, moving,
                    newParentId: targetBucket.ParentBucketId,
                    beforeBucketId: NextSiblingBucketId(project, targetBucket),
                    markerNode: targetNode, markerEdge: KastnDropEdge.After);
            }
        }

        return BucketReparentPlan(project, source, moving, targetBucket, targetNode);
    }

    // The slip half of PlanDrop, mirroring the bucket half: the row under the pointer and
    // its vertical fraction choose the drop. A slip row inserts before (top half) or after
    // (bottom half) that slip with an insertion line; a bucket row appends into the bucket.
    private DropPlan? PlanSlipDrop(DragEventArgs args, ZetlProjectSnapshot project, KastnTreeNode source)
    {
        if (source.Slip is not { } slip
            || IsSlipInDeleted(slip)
            || RowUnderPointer(args) is not { } row)
        {
            return null;
        }

        var (targetNode, fraction) = row;
        var draggedIds = (draggingNodes.Count > 0 ? draggingNodes : [source])
            .Where(node => node.Kind == KastnTreeNodeKind.Slip)
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);

        // Onto a bucket row → append into the bucket (no slip nests inside a slip); a no-op
        // when the dragged set is already last there.
        if (targetNode.Kind == KastnTreeNodeKind.Bucket)
        {
            if (targetNode.Bucket is not { } bucket || KastnWorkbench.IsDeletedBucket(bucket))
            {
                return null;
            }

            var bucketSlips = project.Slips.Where(item => item.BucketId == bucket.Id).ToList();
            var isNoOp = draggedIds.Count > 0 && bucketSlips.Count >= draggedIds.Count
                && bucketSlips.Skip(bucketSlips.Count - draggedIds.Count).All(item => draggedIds.Contains(item.Id));
            return isNoOp
                ? null
                : new DropPlan(DropAction.SlipMove, source, bucket.Id, null, null) { MarkerNode = targetNode };
        }

        // Onto a slip row → before (top half) or after (bottom half) that slip. Dropping
        // onto a member of the dragged set is a no-op.
        if (targetNode.Slip is not { } targetSlip
            || IsSlipInDeleted(targetSlip)
            || draggedIds.Contains(targetSlip.Id))
        {
            return null;
        }

        var slipEdge = EdgeFromFraction(fraction, allowInto: false);
        var beforeSlipId = slipEdge == KastnDropEdge.Before
            ? targetSlip.Id
            : NextSlipInBucket(targetSlip.BucketId, targetSlip.Id);
        return new DropPlan(DropAction.SlipMove, source, targetSlip.BucketId, beforeSlipId, null)
        {
            MarkerNode = targetNode,
            MarkerEdge = slipEdge
        };
    }

    // The fraction of a row's height, at the top and bottom, that triggers an insertion
    // (reorder) instead of a nest, when nesting is possible.
    private const double ReorderEdgeFraction = 0.3;

    // Map the pointer's vertical position within a row to a drop edge. With nesting
    // possible (a bucket target) the middle nests (None); otherwise the row splits in
    // half into before/after, since a slip cannot be nested into.
    private static KastnDropEdge EdgeFromFraction(double fraction, bool allowInto)
    {
        if (!allowInto)
        {
            return fraction < 0.5 ? KastnDropEdge.Before : KastnDropEdge.After;
        }

        if (fraction < ReorderEdgeFraction)
        {
            return KastnDropEdge.Before;
        }

        return fraction > 1 - ReorderEdgeFraction ? KastnDropEdge.After : KastnDropEdge.None;
    }

    private DropPlan? BucketReparentPlan(
        ZetlProjectSnapshot project,
        KastnTreeNode source,
        ZetlBucketSnapshot moving,
        ZetlBucketSnapshot targetBucket,
        KastnTreeNode targetNode)
    {
        if (targetBucket.Id == moving.Id
            || moving.ParentBucketId == targetBucket.Id
            || IsDescendant(project, targetBucket.Id, moving.Id))
        {
            return null;
        }

        var markerNode = targetNode.Kind == KastnTreeNodeKind.Bucket
            ? targetNode
            : FindTreeNode(projectTree.ItemsSource as IEnumerable<KastnTreeNode>, targetBucket.Id);
        return new DropPlan(DropAction.BucketReparent, source, null, null, targetBucket.Id)
        {
            MarkerNode = markerNode
        };
    }

    private DropPlan? BucketReorderPlan(
        ZetlProjectSnapshot project,
        KastnTreeNode source,
        ZetlBucketSnapshot moving,
        string? newParentId,
        string? beforeBucketId,
        KastnTreeNode? markerNode,
        KastnDropEdge markerEdge)
    {
        // Reordering before/after itself is a no-op, as is landing in the same parent
        // immediately before the slot it already holds.
        if (beforeBucketId == moving.Id)
        {
            return null;
        }

        // The new parent must not be the bucket itself or one of its descendants.
        if (newParentId is not null
            && (newParentId == moving.Id || IsDescendant(project, newParentId, moving.Id)))
        {
            return null;
        }

        return new DropPlan(DropAction.BucketReorder, source, null, null, newParentId)
        {
            BeforeBucketId = beforeBucketId,
            MarkerNode = markerNode,
            MarkerEdge = markerEdge
        };
    }

    private static string? NextSiblingBucketId(ZetlProjectSnapshot project, ZetlBucketSnapshot bucket)
    {
        var siblings = project.Buckets
            .Where(item => item.ParentBucketId == bucket.ParentBucketId)
            .ToList();
        var index = siblings.FindIndex(item => item.Id == bucket.Id);
        return index >= 0 && index + 1 < siblings.Count ? siblings[index + 1].Id : null;
    }

    // The node under the pointer and the pointer's vertical fraction (0 = top, 1 = bottom)
    // within that row, used to choose between inserting before/after and nesting into it.
    // Works for tree rows and board cards/columns, which all carry the "dragRow" tag.
    private (KastnTreeNode Node, double Fraction)? RowUnderPointer(DragEventArgs args)
    {
        var visual = args.Source as Visual;
        while (visual is not null)
        {
            if (visual is Control { Tag: "dragRow", DataContext: KastnTreeNode node } rowControl)
            {
                var height = rowControl.Bounds.Height;
                var fraction = height > 0
                    ? Math.Clamp(args.GetPosition(rowControl).Y / height, 0, 1)
                    : 0.5;
                return (node, fraction);
            }

            visual = visual.GetVisualParent();
        }

        return null;
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
            else if (plan.Action == DropAction.BucketReorder)
            {
                await ApplyBucketReorderAsync(plan);
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

    // The shared inner step of both move paths: send one mutation and, on success, read the
    // record's new revision from the response so it can be threaded into the next command
    // (a reorder after a move/reparent). Slip and bucket snapshots both carry a revision.
    private async Task<(long Revision, ZetlResponseEnvelope Response)> SendThreadedAsync(
        ZetlCommandEnvelope command,
        long revision)
    {
        var response = await ExecuteMutationAsync(command);
        var next = response.Status == ZetlResponseStatus.Success
            ? response.Payload?.Deserialize<RevisionCarrier>(ZetlProtocolJson.Options)?.Revision ?? revision
            : revision;
        return (next, response);
    }

    private sealed record RevisionCarrier
    {
        public long Revision { get; init; }
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
        // Group the whole drag into one undo entry; it disposes at method end, after
        // the final refresh, so the recorded positions read from fresh project state.
        using var undoGesture = BeginGesture("Move slips");
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
                    var (rev, moveResponse) = await SendThreadedAsync(
                        ZetlCommandEnvelope.Create(
                            Guid.NewGuid().ToString("N"),
                            ZetlCommandKind.MoveSlip,
                            new MoveSlipCommand { DestinationBucketId = plan.DestinationBucketId! },
                            project.Id,
                            slip.Id,
                            revision),
                        revision);
                    if (moveResponse.Status != ZetlResponseStatus.Success)
                    {
                        failure = moveResponse.Error?.Message ?? $"Move failed: {moveResponse.Status}.";
                        break;
                    }

                    revision = rev;
                }

                var isSameBucket = slip.BucketId == plan.DestinationBucketId;
                var needsEndReorder = isSameBucket && plan.BeforeSlipId is null &&
                                      (slips.Count > 1 || project.Slips.LastOrDefault(s => s.BucketId == plan.DestinationBucketId)?.Id != slip.Id);

                if ((plan.BeforeSlipId is { } beforeId && !string.Equals(beforeId, slip.Id, StringComparison.Ordinal))
                    || needsEndReorder)
                {
                    var (_, reorderResponse) = await SendThreadedAsync(
                        ZetlCommandEnvelope.Create(
                            Guid.NewGuid().ToString("N"),
                            ZetlCommandKind.ReorderSlip,
                            new ReorderSlipCommand { BeforeSlipId = plan.BeforeSlipId },
                            project.Id,
                            slip.Id,
                            revision),
                        revision);
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
        var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
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

    // Reorder a bucket to a sibling position. When the target slot is under a different
    // parent, reparent first (UpdateBucket) and thread the new revision into the
    // ReorderBucket so the sibling move sees an up-to-date record.
    private async Task ApplyBucketReorderAsync(DropPlan plan)
    {
        if (currentProject is not { } project || plan.Source.Bucket is not { } bucket)
        {
            return;
        }

        pendingBucketSelectionId = bucket.Id;
        var revision = bucket.Revision;

        if (bucket.ParentBucketId != plan.NewParentBucketId)
        {
            var (rev, reparent) = await SendThreadedAsync(
                ZetlCommandEnvelope.Create(
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
                    revision),
                revision);
            if (reparent.Status != ZetlResponseStatus.Success)
            {
                HandleSimpleResponse(reparent, "Bucket moved.");
                return;
            }

            revision = rev;
        }

        var (_, response) = await SendThreadedAsync(
            ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.ReorderBucket,
                new ReorderBucketCommand { BeforeBucketId = plan.BeforeBucketId },
                project.Id,
                bucket.Id,
                revision),
            revision);
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
        stickyDropPlan = null;
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
            stickyDropPlan = null;
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

        var resolved = PlanDrop(args);
        if (resolved is not null)
        {
            stickyDropPlan = resolved;
        }

        args.DragEffects = (resolved ?? stickyDropPlan) is not null
            ? DragDropEffects.Move
            : DragDropEffects.None;
        args.Handled = true;
    }

    private void OnBoardDragLeave(object? sender, RoutedEventArgs args)
    {
        boardDragPointerInsideBoard = false;
        args.Handled = true;
    }

    private async void OnBoardDrop(object? sender, DragEventArgs args)
    {
        if ((PlanDrop(args) ?? stickyDropPlan) is not { } plan)
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
