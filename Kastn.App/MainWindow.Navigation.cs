using Avalonia.Controls;

namespace KASTN;

internal partial class MainWindow
{
    private KastnNavigationCoordinator CreateNavigationCoordinator() => new(editorState,
        () => new(currentProject?.Id, editHistory.Generation, connection?.NavigationVersion ?? 0,
            connection?.Current.ServerInstanceId, lifetime.IsRetired || lifetime.AllowClose),
        SaveEditorAsync, id => connection.NavigateToProjectAsync(id));

    private async void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (projectTree.IsReconciling || refreshing || SelectedTreeNode is not { } node) return;
        var ids = projectTree.SelectedItems?.OfType<KastnTreeNode>().Select(item => item.Id).ToArray() ?? [node.Id];
        var result = await navigation.SelectTreeAsync(ids, CurrentSelection());
        if (result is null) return;
        var wasRefreshing = refreshing;
        refreshing = true;
        try { ApplyTreeNodeSelection(result.Ids); }
        finally { refreshing = wasRefreshing; }
        if (result.Accepted) UpdateTreeSelectionUi();
        else
        {
            RefreshBucketEditor();
            RefreshViewer();
            RefreshDestinationBuckets();
        }
    }

    private void ReselectSlipNode(string slipId) => SelectSlipNode(slipId, force: true);

    private void SelectSlipNode(string slipId, bool force = false)
    {
        if (lifetime.IsRetired || treeProjection.Find(slipId) is not { } node) return;
        if (!force && ReferenceEquals(projectTree.SelectedItem, node)) return;
        // Mutation completions may need to rebind a node already restored by a
        // snapshot. Ordinary reader/board links retain an unchanged selection.
        if (force) projectTree.SelectedItem = null;
        projectTree.SelectedItem = node;
    }

    private Action<string> CaptureSlipSelectionAction()
    {
        var projectId = currentProject?.Id;
        var generation = editHistory.Generation;
        return slipId =>
        {
            if (projectId is null || currentProject?.Id != projectId || generation != editHistory.Generation) return;
            SelectSlipNode(slipId);
        };
    }

}
