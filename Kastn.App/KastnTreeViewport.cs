using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace KASTN;

// Large trees present the expanded hierarchy as one flat viewport. Selection and
// navigation use the model, including rows that have no live container. Small
// trees keep native hierarchy and the existing move-preserving items panel.
internal sealed class KastnTreeViewport : TreeView
{
    private static readonly FuncTemplate<Panel?> Small = new(() => new KastnTreeItemsPanel());
    private static readonly FuncTemplate<Panel?> Large = new(() => new KastnFlatTreePanel());
    private IReadOnlyList<KastnTreeNode> roots = [];
    private readonly Dictionary<KastnTreeNode, KastnTreeNode?> parents = [];
    private IReadOnlyList<KastnTreeNode> visible = [];
    private bool virtualized;
    private bool changingHierarchy;
    private bool selectingRange;
    private KastnTreeNode? rangeAnchor;
    private long selectionVersion;
    public bool IsReconciling => changingHierarchy;
    protected override Type StyleKeyOverride => typeof(TreeView);

    public KastnTreeViewport() => AddHandler(KeyDownEvent, OnNavigationKey, RoutingStrategies.Tunnel);

    public void SetHierarchy(IReadOnlyList<KastnTreeNode> nodes)
    {
        var selection = SelectedItems.OfType<KastnTreeNode>().ToArray();
        changingHierarchy = true;
        try
        {
            foreach (var old in parents.Keys) old.PropertyChanged -= OnNodeChanged;
            roots = nodes;
            parents.Clear();
            Add(nodes, null, 0);
            virtualized = parents.Count >= 128;
            foreach (var node in parents.Keys)
            {
                node.IsFlatTree = virtualized;
                node.PropertyChanged += OnNodeChanged;
            }
            var template = virtualized ? Large : Small;
            if (!ReferenceEquals(ItemsPanel, template)) { ItemsPanel = template; UpdateLayout(); }
            if (virtualized) RefreshVisible();
            else
            {
                visible = [];
                if (!ReferenceEquals(ItemsSource, nodes)) ItemsSource = nodes;
            }
            if (parents.Count == 0) SelectedItem = null;
            RestoreSelection(selection);
            if (rangeAnchor is not null && !parents.ContainsKey(rangeAnchor)) rangeAnchor = null;
        }
        finally { changingHierarchy = false; }

        void Add(IEnumerable<KastnTreeNode> level, KastnTreeNode? parent, int depth)
        {
            foreach (var node in level)
            {
                parents[node] = parent;
                node.TreeDepth = depth;
                Add(node.Children, node, depth + 1);
            }
        }
    }

    private IEnumerable<KastnTreeNode> VisibleNodes(IEnumerable<KastnTreeNode> level)
    {
        foreach (var node in level)
        {
            yield return node;
            if (node.IsExpanded)
                foreach (var child in VisibleNodes(node.Children)) yield return child;
        }
    }

    private void RefreshVisible()
    {
        var desired = VisibleNodes(roots).ToArray();
        if (visible.SequenceEqual(desired) && ReferenceEquals(ItemsSource, visible)) return;
        var selection = SelectedItems.OfType<KastnTreeNode>().ToArray();
        visible = desired;
        ItemsSource = visible;
        RestoreSelection(selection);
    }

    private void RestoreSelection(IEnumerable<KastnTreeNode> previous)
    {
        var present = previous.Where(parents.ContainsKey).ToArray();
        if (SelectedItems.Cast<object>().SequenceEqual(present)) return;
        SelectedItems.Clear();
        AddSelection(present);
    }

    private void OnNodeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!virtualized || changingHierarchy || e.PropertyName != nameof(KastnTreeNode.IsExpanded)) return;
        var anchor = CaptureAnchor();
        changingHierarchy = true;
        try { RefreshVisible(); RestoreAnchor(anchor); }
        finally { changingHierarchy = false; }
    }

    internal (KastnTreeNode Node, double Y)? CaptureAnchor()
    {
        if (!virtualized || this.GetVisualRoot() is null) return null;
        (KastnTreeNode Node, double Y)? result = null;
        foreach (var row in GetRealizedContainers().OfType<TreeViewItem>())
        {
            if (row.DataContext is not KastnTreeNode node || row.TranslatePoint(default, this) is not { } point
                || point.Y + row.Bounds.Height <= 0 || point.Y >= Bounds.Height) continue;
            if (result is null || point.Y < result.Value.Y) result = (node, point.Y);
        }
        return result;
    }

    internal void RestoreAnchor((KastnTreeNode Node, double Y)? anchor)
    {
        if (!virtualized || anchor is not { } saved || !visible.Contains(saved.Node)) return;
        var row = ShowNode(saved.Node);
        UpdateLayout();
        var scroll = this.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (row?.TranslatePoint(default, this) is { } point && scroll is not null)
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + point.Y - saved.Y));
    }

    public TreeViewItem? ShowNode(KastnTreeNode node)
    {
        if (!parents.ContainsKey(node) || this.GetVisualRoot() is null) return null;
        if (virtualized)
        {
            var wasReconciling = changingHierarchy;
            changingHierarchy = true;
            try
            {
                var expanded = false;
                for (var parent = parents[node]; parent is not null; parent = parents[parent])
                    if (!parent.IsExpanded) { parent.IsExpanded = true; expanded = true; }
                if (expanded) RefreshVisible();
            }
            finally { changingHierarchy = wasReconciling; }
            UpdateLayout();
            var index = visible.ToList().IndexOf(node);
            if (index < 0) return null;
            if (index == 0 && ItemsPanelRoot is { } panel)
            {
                panel.BringIntoView(new Rect(0, 0, panel.Bounds.Width, 1));
                UpdateLayout();
            }
            ScrollIntoView(index);
            UpdateLayout();
            return ContainerFromIndex(index) as TreeViewItem;
        }
        var path = new Stack<KastnTreeNode>();
        for (var current = node; current is not null; current = parents.GetValueOrDefault(current)) path.Push(current);
        ItemsControl owner = this;
        TreeViewItem? row = null;
        while (path.TryPop(out var current))
        {
            if (owner is TreeViewItem parent) parent.IsExpanded = true;
            owner.UpdateLayout();
            row = owner.ContainerFromIndex(owner.Items.IndexOf(current)) as TreeViewItem;
            if (row is null) return null;
            owner = row;
        }
        row?.BringIntoView();
        return row;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SelectedItemProperty) return;
        var version = ++selectionVersion;
        if (changingHierarchy) return;
        if (!selectingRange) rangeAnchor = SelectedItem as KastnTreeNode;
        if (!virtualized || !AutoScrollToSelectedItem || SelectedItem is not KastnTreeNode selected) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (selectionVersion == version && ReferenceEquals(SelectedItem, selected)) ShowNode(selected);
        }, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        selectionVersion++;
        foreach (var node in parents.Keys) node.PropertyChanged -= OnNodeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        foreach (var node in parents.Keys)
        {
            node.PropertyChanged -= OnNodeChanged;
            node.PropertyChanged += OnNodeChanged;
        }
    }

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        var needs = base.NeedsContainerOverride(item, index, out recycleKey);
        recycleKey = null;
        return needs;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.Handled && virtualized && e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            && NodeFrom(e.Source) is { } node && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            SelectRange(node);
            e.Handled = true;
        }
        else base.OnPointerPressed(e);
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        if (virtualized && e.NavigationMethod == NavigationMethod.Directional && NodeFrom(e.Source) is { } node)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) SelectRange(node);
            else SelectedItem = node;
            e.Handled = true;
        }
        else base.OnGotFocus(e);
    }

    private void SelectRange(KastnTreeNode target)
    {
        var nodes = visible.ToList();
        var anchor = rangeAnchor ?? SelectedItem as KastnTreeNode ?? target;
        var first = nodes.IndexOf(anchor);
        var last = nodes.IndexOf(target);
        if (first < 0 || last < 0) { SelectedItem = target; return; }
        selectingRange = true;
        try
        {
            var desired = nodes.Skip(Math.Min(first, last)).Take(Math.Abs(last - first) + 1).ToArray();
            foreach (var stale in SelectedItems.Cast<object>().Where(item => !desired.Contains(item)).ToArray()) SelectedItems.Remove(stale);
            AddSelection(desired);
        }
        finally { selectingRange = false; rangeAnchor = anchor; }
    }

    private void AddSelection(IEnumerable<KastnTreeNode> nodes)
    {
        var added = nodes.Where(node => !SelectedItems.Contains(node)).Cast<object>().ToArray();
        if (SelectedItems is AvaloniaList<object> selection) selection.AddRange(added);
        else foreach (var node in added) SelectedItems.Add(node);
    }

    public new void SelectAll()
    {
        if (!virtualized) base.SelectAll();
        else AddSelection(parents.Keys);
    }

    private void OnNavigationKey(object? sender, KeyEventArgs e)
    {
        if (!virtualized || e.Handled || NodeFrom(e.Source) is not { } from) return;
        if (e.Key == Key.Enter && e.Source is Visual source
            && source.GetSelfAndVisualAncestors().TakeWhile(visual => visual is not TreeViewItem).Any(visual => visual is Button)) return;
        if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { SelectAll(); e.Handled = true; return; }
        KastnTreeNode? target = null;
        if (e.Key is Key.Left or Key.Right or Key.Enter or Key.Add or Key.Subtract or Key.Multiply or Key.Divide)
        {
            var expand = e.Key is Key.Right or Key.Add or Key.Multiply || e.Key == Key.Enter && !from.IsExpanded;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key is Key.Multiply or Key.Divide)
            {
                changingHierarchy = true;
                try { SetExpanded(from, expand); RefreshVisible(); }
                finally { changingHierarchy = false; }
            }
            else if (from.Children.Count > 0 && from.IsExpanded != expand) from.IsExpanded = expand;
            else if (e.Key is Key.Left or Key.Right)
                target = expand ? from.Children.FirstOrDefault() : parents.GetValueOrDefault(from);
        }
        else if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            var index = visible.ToList().IndexOf(from);
            var step = e.Key is Key.PageUp or Key.PageDown ? Math.Max(1, (int)(Bounds.Height / KastnFlatTreePanel.RowHeight)) : 1;
            var next = e.Key switch
            {
                Key.Home => 0, Key.End => visible.Count - 1,
                Key.Up or Key.PageUp => index - step, _ => index + step
            };
            if (visible.Count > 0) target = visible[Math.Clamp(next, 0, visible.Count - 1)];
        }
        else return;
        e.Handled = true;
        if (target is null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) SelectRange(target);
        else SelectedItem = target;
        ShowNode(target)?.Focus(NavigationMethod.Tab);
    }

    private static void SetExpanded(KastnTreeNode node, bool expanded)
    {
        node.IsExpanded = expanded;
        foreach (var child in node.Children) SetExpanded(child, expanded);
    }
    private static KastnTreeNode? NodeFrom(object? source) => source is Visual visual
        ? visual.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.DataContext as KastnTreeNode : null;
}
