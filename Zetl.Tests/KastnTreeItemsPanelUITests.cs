using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KASTN;
using Xunit;

namespace ZETL.Tests;

public class KastnTreeItemsPanelUITests
{
    [AvaloniaFact]
    public void MovesKeepHeadersFocusSelectionAndLogicalAttachment()
    {
        using var h = new TreeHarness();
        var nodes = Enumerable.Range(0, 30).Select(i => Node($"slip-{i}")).ToArray();
        var source = new ObservableCollection<KastnTreeNode>(nodes);
        h.SetSource(source);
        var rows = h.Tree.GetRealizedContainers().Cast<TreeViewItem>().ToArray();
        var header = rows[0].GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == nodes[0].Label);
        h.Tree.SelectedItems.Add(nodes[0]);
        h.Tree.SelectedItems.Add(nodes[2]);
        Assert.True(rows[0].Focus());
        var detached = 0;
        foreach (var row in rows) row.DetachedFromLogicalTree += (_, _) => detached++;
        source.Move(0, 29);
        source.Move(12, 0);
        h.Layout();
        Assert.Equal(0, detached);
        Assert.Same(rows[0], h.Tree.ContainerFromIndex(29));
        Assert.Same(rows[13], h.Tree.ContainerFromIndex(0));
        Assert.Same(header, rows[0].GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == nodes[0].Label));
        Assert.True(rows[0].IsFocused);
        Assert.True(rows[0].IsSelected);
        Assert.True(rows[2].IsSelected);
        Assert.Equal(2, h.Tree.SelectedItems.Count);
        AssertOrder(h.Tree, source);
        Assert.True(rows[13].Bounds.Y < rows[0].Bounds.Y);
    }

    [AvaloniaFact]
    public void AddRemoveReplaceAndResetUpdateContainerIndexesAndRetireRemovedHeaders()
    {
        using var h = new TreeHarness();
        var source = new ObservableCollection<KastnTreeNode>([Node("one"), Node("two"), Node("three")]);
        h.SetSource(source);
        var two = Assert.IsType<TreeViewItem>(h.Tree.ContainerFromIndex(1));
        source.Insert(1, Node("inserted"));
        h.Layout();
        Assert.Same(two, h.Tree.ContainerFromIndex(2));
        AssertOrder(h.Tree, source);
        source.RemoveAt(1);
        source[1] = Node("replacement");
        h.Layout();
        Assert.Null(two.Parent);
        Assert.Null(two.Header);
        AssertOrder(h.Tree, source);
        source.Clear();
        h.Layout();
        Assert.Empty(h.Tree.GetRealizedContainers());
        Assert.Null(h.Tree.ContainerFromIndex(0));
        source.Add(Node("after-reset"));
        h.Layout();
        AssertOrder(h.Tree, source);
        var previous = Assert.IsType<TreeViewItem>(h.Tree.ContainerFromIndex(0));
        h.SetSource(new([Node("another-project")]));
        Assert.Null(previous.Parent);
        Assert.Null(previous.Header);
        Assert.Equal("another-project", Assert.IsType<KastnTreeNode>(h.Tree.ContainerFromIndex(0)!.DataContext).Id);
    }

    [AvaloniaFact]
    public void NestedBucketMovesPreserveCollapseStateAndChildren()
    {
        using var h = new TreeHarness();
        var first = Node("bucket-one", Node("one"), Node("two"));
        var second = Node("bucket-two", Node("three"));
        var source = new ObservableCollection<KastnTreeNode>([first, second]);
        h.SetSource(source);
        var firstRow = Assert.IsType<TreeViewItem>(h.Tree.ContainerFromIndex(0));
        var child = Assert.IsType<TreeViewItem>(firstRow.ContainerFromIndex(0));
        var secondRow = Assert.IsType<TreeViewItem>(h.Tree.ContainerFromIndex(1));
        firstRow.IsExpanded = false;
        source.Move(0, 1);
        h.Layout();
        Assert.Same(firstRow, h.Tree.ContainerFromIndex(1));
        Assert.Same(secondRow, h.Tree.ContainerFromIndex(0));
        Assert.False(firstRow.IsExpanded);
        firstRow.IsExpanded = true;
        first.Children.Move(0, 1);
        h.Layout();
        Assert.Same(child, firstRow.ContainerFromIndex(1));
        AssertOrder(firstRow, first.Children);
        second.Children.Add(first.Children[1]);
        first.Children.RemoveAt(1);
        h.Layout();
        Assert.Null(child.Parent);
        AssertOrder(firstRow, first.Children);
        AssertOrder(secondRow, second.Children);
    }

    [AvaloniaFact]
    public void NavigationAndBringIntoViewUseTheNewRowOrder()
    {
        using var h = new TreeHarness();
        var source = new ObservableCollection<KastnTreeNode>(Enumerable.Range(0, 40).Select(i => Node($"note-{i}")));
        h.SetSource(source);
        source.Move(39, 0);
        h.Layout();
        var first = h.Tree.ContainerFromIndex(0)!;
        var last = h.Tree.ContainerFromIndex(39)!;
        var panel = Assert.Single(h.Tree.GetVisualDescendants().OfType<KastnTreeItemsPanel>().Where(panel => panel.Children.Count == source.Count));
        var navigation = (INavigableContainer)panel;
        Assert.Same(first, navigation.GetControl(NavigationDirection.First, null, false));
        Assert.Same(last, navigation.GetControl(NavigationDirection.Last, null, false));
        Assert.Same(h.Tree.ContainerFromIndex(1), navigation.GetControl(NavigationDirection.Down, first, false));
        Assert.Same(first, navigation.GetControl(NavigationDirection.Next, last, true));
        Assert.Null(navigation.GetControl(NavigationDirection.Previous, first, false));
        Assert.Same(last, navigation.GetControl(NavigationDirection.Previous, first, true));
        last.BringIntoView();
        h.Layout();
        Assert.True(h.Tree.GetVisualDescendants().OfType<ScrollViewer>().First().Offset.Y > 0);
    }

    [AvaloniaFact]
    public void ItemsThatAreTheirOwnContainerKeepTheirHeadersWhenRemoved()
    {
        using var h = new TreeHarness();
        var own = new TreeViewItem { Header = "Own row", DataContext = "context" };
        var source = new ObservableCollection<TreeViewItem>([own]);
        h.Tree.ItemsSource = source;
        h.Layout();
        Assert.Same(own, h.Tree.ContainerFromIndex(0));
        source.Clear();
        h.Layout();
        Assert.Null(own.Parent);
        Assert.Equal("Own row", own.Header);
    }

    private static KastnTreeNode Node(string id, params KastnTreeNode[] children) => new()
    {
        Id = id, Label = id, Kind = children.Length == 0 ? KastnTreeNodeKind.Slip : KastnTreeNodeKind.Bucket,
        Children = new(children)
    };

    private static void AssertOrder(ItemsControl owner, IEnumerable<KastnTreeNode> source)
    {
        var nodes = source.ToArray();
        Assert.Equal(nodes.Length, owner.GetRealizedContainers().Count());
        for (var i = 0; i < nodes.Length; i++)
        {
            var row = owner.ContainerFromIndex(i)!;
            Assert.Same(nodes[i], row.DataContext);
            Assert.Equal(i, owner.IndexFromContainer(row));
        }
    }

    private sealed class TreeHarness : IDisposable
    {
        public TreeView Tree { get; } = new() { SelectionMode = SelectionMode.Multiple, AutoScrollToSelectedItem = false };
        private readonly Window window;

        public TreeHarness()
        {
            var panel = new FuncTemplate<Panel?>(() => new KastnTreeItemsPanel());
            Tree.ItemsPanel = panel;
            Tree.ItemTemplate = new FuncTreeDataTemplate<KastnTreeNode>(
                (node, _) => new TextBlock { Text = node.Label }, node => node.Children);
            Tree.Styles.Add(new Style(selector => selector.OfType<TreeViewItem>())
            {
                Setters = { new Setter(TreeViewItem.IsExpandedProperty, true), new Setter(ItemsControl.ItemsPanelProperty, panel) }
            });
            window = new Window { Width = 320, Height = 220, Content = Tree };
            window.Show();
        }

        public void SetSource(ObservableCollection<KastnTreeNode> nodes) { Tree.ItemsSource = nodes; Layout(); }
        public void Layout() { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        public void Dispose() { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }
}

public partial class ZetlUITests
{
    [AvaloniaFact]
    public void SnapshotReorderKeepsActualTreeRowsAndGroupSelectionWithFreshRevisions()
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            window.UpdateLayout();
            var rows = window.projectTree.GetVisualDescendants().OfType<TreeViewItem>()
                .Where(row => row.DataContext is KastnTreeNode).ToDictionary(row => ((KastnTreeNode)row.DataContext!).Id);
            Assert.Equal(3, rows.Count);
            window.projectTree.SelectedItems.Clear();
            window.projectTree.SelectedItems.Add(window.treeProjection.Find("render-one"));
            window.projectTree.SelectedItems.Add(window.treeProjection.Find("render-two"));
            PublishRenderSnapshot(controller, project with
            {
                ChangeSequence = 2,
                Slips = project.Slips.Reverse().Select(slip => slip with { Revision = 2 }).ToArray()
            });
            window.UpdateLayout();
            foreach (var row in window.projectTree.GetVisualDescendants().OfType<TreeViewItem>())
            {
                var node = Assert.IsType<KastnTreeNode>(row.DataContext);
                Assert.Same(rows[node.Id], row);
                if (node.Slip is { } slip) Assert.Equal(2, slip.Revision);
            }
            Assert.Equal(new[] { "render-one", "render-two" }, window.projectTree.SelectedItems.Cast<KastnTreeNode>().Select(node => node.Id).Order());
        }
        finally { CloseWindow(window); }
    }
}
