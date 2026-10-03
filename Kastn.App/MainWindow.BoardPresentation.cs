using Avalonia.Controls;
using Avalonia.Input;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    private KastnBoardRenderInputs CaptureBoardInputs(IReadOnlyList<ZetlSlipSnapshot> visible, KastnViewRenderKey key)
    {
        var projectId = ProjectIndex.Project.Id;
        var generation = editHistory.Generation;
        bool IsCurrent() => !boardPresenter.IsDisposed && currentProject?.Id == projectId
            && editHistory.Generation == generation;
        return new(ProjectIndex, visible, key, showingDeleted, CreateSlipContentRenderer(),
            new(ThemeBrush("ZetlBorderBrush"), ThemeBrush("ZetlAccentBrush"),
                ThemeBrush("ZetlSurfaceBrush"), ThemeBrush("ZetlSurfaceAltBrush")),
            id => treeProjection.Find(id),
            new(id => { if (IsCurrent()) ReselectSlipNode(id); },
                id => IsCurrent() && ProjectIndex.Slip(id) is { } slip ? EditBoardSlipAsync(slip) : Task.CompletedTask,
                (bucketId, text) => AddBoardSlipAsync(projectId, bucketId, text, IsCurrent),
                WireBoardCard, WireBoardColumn));
    }

    // Native drag handlers retain the window's gesture state and hit testing.
    // A retired control cannot begin or continue a gesture in a newer board.
    private void WireBoardCard(Control card, Func<bool> canAct)
    {
        card.PointerPressed += (sender, args) => { if (!args.Handled && canAct()) OnBoardCardPointerPressed(sender, args); };
        card.PointerMoved += (sender, args) => { if (canAct()) OnBoardCardPointerMoved(sender, args); };
        card.PointerReleased += (sender, args) => { if (canAct()) OnBoardCardPointerReleased(sender, args); };
        WireBoardDropTarget(card, canAct);
    }

    private void WireBoardColumn(Control header, Control border, Func<bool> canAct)
    {
        header.PointerPressed += (sender, args) => { if (!args.Handled && canAct()) OnBoardColumnHeaderPointerPressed(sender, args); };
        header.PointerMoved += (sender, args) => { if (canAct()) OnBoardColumnHeaderPointerMoved(sender, args); };
        header.PointerReleased += (sender, args) => { if (canAct()) OnBoardColumnHeaderPointerReleased(sender, args); };
        WireBoardDropTarget(border, canAct);
    }

    private void WireBoardDropTarget(Control target, Func<bool> canAct)
    {
        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragOverEvent, (sender, args) => { if (canAct()) OnBoardDragOver(sender, args); });
        target.AddHandler(DragDrop.DragLeaveEvent, (sender, args) => { if (canAct()) OnBoardDragLeave(sender, args); });
        target.AddHandler(DragDrop.DropEvent, (sender, args) => { if (canAct()) OnBoardDrop(sender, args); });
    }

    private async Task<bool> AddBoardSlipAsync(string projectId, string bucketId, string text, Func<bool> isCurrent)
    {
        if (!isCurrent() || ProjectIndex.Bucket(bucketId) is not { } bucket || KastnWorkbench.IsDeletedBucket(bucket))
            return false;
        if (!IsOnline)
        {
            statusText.Text = "Connect to Zetl to edit.";
            return false;
        }
        try
        {
            var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"), ZetlCommandKind.AddSlip,
                new AddSlipCommand { BucketId = bucketId, Text = text, Source = "kastn" }, projectId));
            if (response.Status != ZetlResponseStatus.Success)
            {
                if (isCurrent()) statusText.Text = response.Error?.Message ?? $"Add failed: {response.Status}.";
                return false;
            }
            if (isCurrent())
            {
                await connection.SynchronizeAsync();
                if (isCurrent()) statusText.Text = "Card added.";
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (isCurrent()) statusText.Text = ex.Message;
            return false;
        }
    }
}
