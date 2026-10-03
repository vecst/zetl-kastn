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
    private long dropFeedbackVersion;

    // The last drop position resolved during the current drag. When the pointer slips into
    // a gap between rows (where nothing resolves), the drag holds this position instead of
    // rejecting the drop — so there is no dead band between adjacent slots. It only changes
    // when the pointer moves far enough to resolve a new slot.
    private KastnDropPlan? stickyDropPlan;
    private string? draggingProjectId;
    private bool applyingDrop;
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
        projectTree.AddHandler(DragDrop.DragEnterEvent, OnTreeDragOver);
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
        draggingProjectId = currentProject?.Id;
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
            draggingProjectId = null;
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

        return currentProject is null ? [node] : ProjectIndex.SlipsInDocumentOrder(slipNodes.Select(item => item.Id))
            .Select(slip => treeProjection.Find(slip.Id)).OfType<KastnTreeNode>().ToArray();
    }

    private void OnTreeDragOver(object? sender, DragEventArgs args)
    {
        // Remember where the pointer is so the auto-scroll timer can keep scrolling even
        // while the pointer is held still at the edge (DragOver only fires on movement).
        lastDragPointerY = args.GetPosition(projectTree).Y;
        dragPointerInsideTree = true;
        var plan = ResolveDrop(args);
        args.DragEffects = plan is not null ? DragDropEffects.Move : DragDropEffects.None;
        ApplyDropMarker(plan);
        args.Handled = true;
    }

    private void OnTreeDragLeave(object? sender, DragEventArgs args)
    {
        dragPointerInsideTree = false;
        // Native hit testing emits leave/enter when crossing children inside a
        // row, too. Let the matching enter keep or replace the marker before
        // clearing feedback for a real exit from the tree.
        var version = dropFeedbackVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (version == dropFeedbackVersion && !dragPointerInsideTree)
                ApplyDropMarker(null);
        });
    }

    // Show the plan's drag feedback on its marker row — a highlight to nest into, or an
    // insertion line to reorder before/after — clearing the previously marked row.
    private void ApplyDropMarker(KastnDropPlan? plan)
    {
        dropFeedbackVersion++;
        var node = plan?.MarkerId is { } id ? treeProjection.Find(id) : null;
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
        if (ResolveDrop(args) is not { } plan)
        {
            return;
        }

        args.Handled = true;
        await ApplyDropAsync(plan);
    }

    private KastnDropPlan? ResolveDrop(DragEventArgs args)
    {
        var resolved = PlanDrop(args, out var isGap);
        if (resolved is not null) stickyDropPlan = resolved;
        else if (!isGap || stickyDropPlan is { } previous
            && (!KastnDropPlanner.IsValid(ProjectIndex, previous)
                || previous.Action == KastnDropAction.SlipMove && KastnDropPlanner.IsSlipNoOp(ProjectIndex, previous)))
            stickyDropPlan = null;
        return stickyDropPlan;
    }

    private KastnDropPlan? PlanDrop(DragEventArgs args, out bool isGap)
    {
        isGap = false;
        if (draggingNode is not { } source || currentProject is null || !IsOnline
            || draggingProjectId != currentProject.Id || !args.DataTransfer.Contains(DragNodeFormat)) return null;
        var ids = (draggingNodes.Count > 0 ? draggingNodes : [source])
            .Where(node => node.Kind == KastnTreeNodeKind.Slip).Select(node => node.Id).ToArray();
        KastnDropPosition position;
        if (RowUnderPointer(args) is not { } row)
        {
            // Tree empty space promotes/appends a bucket. Slip and board gaps
            // retain the last valid slot; unchanged or invalid rows clear it.
            if (boardDragInProgress || source.Kind == KastnTreeNodeKind.Slip)
            {
                isGap = true;
                return null;
            }
            position = new(KastnDropTargetKind.Empty);
        }
        else
        {
            var (target, fraction, fractionX) = row;
            if (boardDragInProgress && source.Kind == KastnTreeNodeKind.Bucket)
            {
                var bucketId = target.Kind == KastnTreeNodeKind.Bucket ? target.Id : ProjectIndex.Slip(target.Id)?.BucketId;
                position = new(KastnDropTargetKind.Bucket, bucketId,
                    fractionX < 0.5 ? KastnDropEdge.Before : KastnDropEdge.After);
            }
            else if (boardDragInProgress && target.Kind == KastnTreeNodeKind.Bucket
                && source.Kind == KastnTreeNodeKind.Slip)
                position = BoardColumnSlipPosition(args, target.Id, ids.ToHashSet(StringComparer.Ordinal));
            else
                position = new(target.Kind == KastnTreeNodeKind.Slip ? KastnDropTargetKind.Slip : KastnDropTargetKind.Bucket,
                    target.Id, source.Kind == KastnTreeNodeKind.Slip && target.Kind == KastnTreeNodeKind.Bucket
                        ? KastnDropEdge.None : EdgeFromFraction(fraction, target.Kind == KastnTreeNodeKind.Bucket),
                    MarkerId: boardDragInProgress ? target.Id : null);
        }
        return KastnDropPlanner.Plan(ProjectIndex, source.Id, source.Kind == KastnTreeNodeKind.Slip, ids, position);
    }

    private KastnDropPosition BoardColumnSlipPosition(DragEventArgs args, string bucketId, HashSet<string> draggedIds)
    {
        if (boardPresenter.ColumnCards(bucketId) is not { } cardsPanel)
            return new(KastnDropTargetKind.Bucket, bucketId);
        var y = args.GetPosition(cardsPanel).Y;
        string? last = null;
        foreach (var child in cardsPanel.Children)
        {
            if (child is not Control { DataContext: KastnTreeNode node } control || draggedIds.Contains(node.Id)) continue;
            last = node.Id;
            if (y < control.Bounds.Y + control.Bounds.Height / 2)
                return new(KastnDropTargetKind.BoardSlot, bucketId, KastnDropEdge.Before, node.Id, node.Id);
        }
        return last is null ? new(KastnDropTargetKind.Bucket, bucketId)
            : new(KastnDropTargetKind.BoardSlot, bucketId, KastnDropEdge.After, MarkerId: last);
    }

    private static KastnDropEdge EdgeFromFraction(double fraction, bool allowInto) => !allowInto
        ? fraction < 0.5 ? KastnDropEdge.Before : KastnDropEdge.After
        : fraction < 0.3 ? KastnDropEdge.Before : fraction > 0.7 ? KastnDropEdge.After : KastnDropEdge.None;

    // The node under the pointer and the pointer's fraction within that row —
    // vertical (0 = top, 1 = bottom) for tree rows and cards, horizontal
    // (0 = left, 1 = right) for side-by-side board columns — used to choose
    // between inserting before/after and nesting into it. Works for tree rows
    // and board cards/columns, which all carry the "dragRow" tag.
    private (KastnTreeNode Node, double Fraction, double FractionX)? RowUnderPointer(DragEventArgs args)
    {
        var visual = args.Source as Visual;
        while (visual is not null)
        {
            if (visual is Control { Tag: "dragRow", DataContext: KastnTreeNode node } rowControl)
            {
                var position = args.GetPosition(rowControl);
                var height = rowControl.Bounds.Height;
                var fraction = height > 0 ? Math.Clamp(position.Y / height, 0, 1) : 0.5;
                var width = rowControl.Bounds.Width;
                var fractionX = width > 0 ? Math.Clamp(position.X / width, 0, 1) : 0.5;
                return (node, fraction, fractionX);
            }

            visual = visual.GetVisualParent();
        }

        return null;
    }

    private async Task ApplyDropAsync(KastnDropPlan plan)
    {
        if (applyingDrop || !IsOnline || currentProject?.Id != plan.ProjectId
            || saving && inflightSave is not { IsCompleted: false }) return;
        plan = plan with { SlipIds = plan.SlipIds.ToArray() };
        var context = new KastnEditorWorkflowContext(plan.ProjectId, editorState);
        var generation = editHistory.Generation;
        var ownsBusy = false;
        applyingDrop = true;
        try
        {
            var saved = await SaveEditorAsync();
            if (!context.IsSameSession(currentProject?.Id, editorState) || generation != editHistory.Generation) return;
            if (!saved)
            {
                statusText.Text = "Save or resolve the current slip before moving things.";
                return;
            }
            if (!IsOnline || saving || !TrySettleSavedEditorSnapshot(plan.ProjectId)
                || !context.IsSameSession(currentProject?.Id, editorState) || editorState.IsDirty) return;
            if (!KastnDropPlanner.IsValid(ProjectIndex, plan))
            {
                statusText.Text = "The drop target changed. Try the move again.";
                return;
            }
            if (plan.Action == KastnDropAction.SlipMove && KastnDropPlanner.IsSlipNoOp(ProjectIndex, plan)) return;
            var operation = new KastnMoveOperation(ProjectIndex, plan);
            ownsBusy = true;
            saving = true;
            SetEditingEnabled();
            // Deferring refresh coalesces ordinary mutation events without
            // suppressing project navigation or explicit snapshots.
            using var gesture = BeginGesture(plan.Action == KastnDropAction.SlipMove ? "Move slips" : "Move bucket");
            await using var refreshBatch = connection.DeferRefresh();
            var result = await operation.ExecuteAsync(async (command, payload) =>
            {
                var mutation = plan.Action == KastnDropAction.SlipMove
                    ? new KastnEditorMutationAcceptance(editorState, command, payload) : null;
                pendingEditorMutation = mutation;
                try
                {
                    var response = await ExecuteMutationAsync(command);
                    if (context.IsSameSession(currentProject?.Id, editorState) && mutation is not null)
                    {
                        if (KastnMoveOperation.IsConfirmedResponse(plan, command, response) && mutation.TryAcceptResponse(response)
                            && mutation.IsCurrentEditor)
                        {
                            PersistEditorAfterSave(plan.ProjectId);
                            UpdateEditorFromState();
                        }
                        else if (mutation.ReconcileConflict(response)) ShowConflict();
                    }
                    return response;
                }
                finally { pendingEditorMutation = null; }
            }, () => IsOnline && context.IsSameSession(currentProject?.Id, editorState)
                && generation == editHistory.Generation && editorState.ConflictCurrent is null
                && KastnDropPlanner.IsValid(ProjectIndex, plan));
            if (!context.IsSameSession(currentProject?.Id, editorState) || generation != editHistory.Generation) return;
            await connection.SynchronizeAsync();
            if (!context.IsSameSession(currentProject?.Id, editorState) || generation != editHistory.Generation) return;
            // SnapshotChanged is posted to the dispatcher. Settle the synchronized
            // snapshot before selection and gesture disposal record final neighbours.
            ApplySnapshot(connection.Current);
            if (!context.IsSameSession(currentProject?.Id, editorState) || generation != editHistory.Generation) return;
            if (result.Status == KastnMoveStatus.Completed && !editorState.IsDirty)
            {
                if (plan.Action == KastnDropAction.SlipMove) ReselectSlipNode(plan.SourceId);
                else projectTree.SelectedItem = treeProjection.Find(plan.SourceId);
            }
            statusText.Text = editorState.ConflictCurrent is not null ? "Resolve the slip conflict before continuing."
                : result.Status != KastnMoveStatus.Completed ? result.ConfirmedCommands > 0
                    ? $"Move partly completed. {result.Error}" : result.Error
                : editorState.IsDirty ? "Unsaved changes."
                : plan.Action != KastnDropAction.SlipMove ? "Bucket moved."
                : result.CompletedItems == 1 ? "Slip moved." : $"{result.CompletedItems} slips moved.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (context.IsSameSession(currentProject?.Id, editorState)) statusText.Text = ex.Message;
        }
        finally
        {
            if (ownsBusy) saving = false;
            applyingDrop = false;
            SetEditingEnabled();
        }
    }

    private void ReselectSlipNode(string slipId)
    {
        var node = treeProjection.Find(slipId);
        if (node is null)
        {
            return;
        }

        // Clearing first guarantees the assignment is a change even when the refresh
        // already restored this node, so OnTreeSelectionChanged fires and re-syncs.
        projectTree.SelectedItem = null;
        projectTree.SelectedItem = node;
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
        await RunBoardDragAsync(node, args);
    }

    // The shared board drag session for cards and column headers: publish the node,
    // run the drag, and clear every marker and flag however the drag ends.
    private async Task RunBoardDragAsync(KastnTreeNode node, PointerEventArgs args)
    {
        draggingNode = node;
        draggingProjectId = currentProject?.Id;
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
            ApplyDropMarker(null);
            stickyDropPlan = null;
            draggingNode = null;
            draggingProjectId = null;
            draggingNodes = [];
            boardDragInProgress = false;
            boardDragPointerInsideBoard = false;
        }
    }

    // Column headers start a bucket drag, mirroring the card handlers; the +
    // button inside the header keeps its click.
    private KastnTreeNode? boardColumnDragCandidate;
    private Point boardColumnDragStart;

    private void OnBoardColumnHeaderPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        boardColumnDragCandidate = null;
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || (args.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if ((sender as Control)?.DataContext is not KastnTreeNode node
            || node.Kind != KastnTreeNodeKind.Bucket
            || !CanDragNode(node))
        {
            return;
        }

        boardColumnDragCandidate = node;
        boardColumnDragStart = args.GetPosition(this);
    }

    private void OnBoardColumnHeaderPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        boardColumnDragCandidate = null;
    }

    private async void OnBoardColumnHeaderPointerMoved(object? sender, PointerEventArgs args)
    {
        if (boardColumnDragCandidate is null || boardDragInProgress || dragInProgress)
        {
            return;
        }

        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            boardColumnDragCandidate = null;
            return;
        }

        var position = args.GetPosition(this);
        if (Math.Abs(position.X - boardColumnDragStart.X) < DragThreshold
            && Math.Abs(position.Y - boardColumnDragStart.Y) < DragThreshold)
        {
            return;
        }

        var node = boardColumnDragCandidate;
        boardColumnDragCandidate = null;
        await RunBoardDragAsync(node, args);
    }

    private void OnBoardDragOver(object? sender, DragEventArgs args)
    {
        lastBoardDragPointerX = args.GetPosition(boardScrollViewer).X;
        boardDragPointerInsideBoard = true;

        var plan = ResolveDrop(args);
        args.DragEffects = plan is not null ? DragDropEffects.Move : DragDropEffects.None;
        ApplyDropMarker(plan);
        args.Handled = true;
    }

    private void OnBoardDragLeave(object? sender, RoutedEventArgs args)
    {
        boardDragPointerInsideBoard = false;
        args.Handled = true;
    }

    private async void OnBoardDrop(object? sender, DragEventArgs args)
    {
        if (ResolveDrop(args) is not { } plan)
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
