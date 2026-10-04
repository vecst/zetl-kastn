using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KASTN;
using Xunit;

namespace ZETL.Tests;

public class KastnTreeViewportUITests
{
    [AvaloniaFact]
    public void TreeViewportBoundsLeafRowsSelectsOffscreenAndRestoresLatestSelection()
    {
        using var h = new Harness();
        var bucket = Bucket("bucket", 1000);
        h.Set([bucket]);
        Assert.InRange(h.Rows.Length, 2, 50);
        Assert.DoesNotContain(h.Rows, row => ReferenceEquals(row.DataContext, bucket.Children[999]));
        h.Tree.SelectedItem = bucket.Children[999];
        h.Layout();
        Assert.True(h.Tree.ShowNode(bucket.Children[999])!.IsSelected);
        Assert.InRange(h.Rows.Length, 2, 50);
        Assert.True(h.Scroll.Offset.Y > h.Scroll.Viewport.Height);
        h.Tree.SelectedItem = bucket.Children[500];
        h.Tree.SelectedItem = bucket.Children[0];
        h.Layout();
        Assert.Contains(h.Rows, row => ReferenceEquals(row.DataContext, bucket.Children[0]));
        Assert.DoesNotContain(h.Rows, row => ReferenceEquals(row.DataContext, bucket.Children[500]));
        Assert.True(h.Scroll.Offset.Y < h.Scroll.Viewport.Height);
    }

    [AvaloniaFact]
    public void TreeViewportKeyboardAndShiftRangeIncludeUnrealizedNodes()
    {
        using var h = new Harness();
        var bucket = Bucket("bucket", 1000);
        h.Set([bucket]);
        h.Tree.SelectedItem = bucket.Children[0];
        h.Layout();
        var row = h.Tree.ShowNode(bucket.Children[0])!;
        Key(row, Avalonia.Input.Key.End, KeyModifiers.Shift);
        h.Layout();
        Assert.Equal(1000, h.Tree.SelectedItems.Count);
        Assert.All(bucket.Children, node => Assert.Contains(node, h.Tree.SelectedItems.Cast<KastnTreeNode>()));
        Assert.InRange(h.Rows.Length, 2, 50);
        row = h.Tree.ShowNode(bucket.Children[999])!;
        Key(row, Avalonia.Input.Key.Up);
        h.Layout();
        Assert.Same(bucket.Children[998], h.Tree.SelectedItem);
        row = h.Tree.ShowNode(bucket.Children[998])!;
        Key(row, Avalonia.Input.Key.Enter);
        Key(row, Avalonia.Input.Key.Subtract);
        h.Layout();
        Assert.Same(bucket.Children[998], h.Tree.SelectedItem);
        Key(row, Avalonia.Input.Key.Home);
        h.Layout();
        Assert.Same(bucket, h.Tree.SelectedItem);
        row = h.Tree.ShowNode(bucket)!;
        Key(row, Avalonia.Input.Key.Left);
        h.Layout();
        Assert.False(bucket.IsExpanded);
        h.Tree.SelectAll();
        Assert.Equal(1001, h.Tree.SelectedItems.Count);
    }

    [AvaloniaFact]
    public void TreeViewportShiftClickKeepsTheAnchorAfterItLeavesTheViewport()
    {
        using var h = new Harness();
        var bucket = Bucket("bucket", 1000);
        h.Set([bucket]);
        h.Tree.SelectedItem = bucket.Children[10];
        h.Layout();
        var row = h.Tree.ShowNode(bucket.Children[700])!;
        Click(row, KeyModifiers.Shift);
        h.Layout();
        Assert.Equal(691, h.Tree.SelectedItems.Count);
        Assert.Contains(bucket.Children[10], h.Tree.SelectedItems.Cast<KastnTreeNode>());
        Assert.Contains(bucket.Children[699], h.Tree.SelectedItems.Cast<KastnTreeNode>());
        Assert.InRange(h.Rows.Length, 2, 50);
    }

    [AvaloniaFact]
    public void TreeViewportKeepsVisibleRowIdentityFocusAndAnchorAcrossMovesAndResize()
    {
        using var h = new Harness();
        var bucket = Bucket("bucket", 1000);
        var source = new ObservableCollection<KastnTreeNode>([bucket]);
        h.Set(source);
        var node = bucket.Children[3];
        h.Tree.SelectedItem = node;
        h.Layout();
        var focused = h.Tree.ShowNode(node)!;
        Assert.True(focused.Focus());
        var first = h.Tree.ShowNode(bucket.Children[0])!;
        var next = h.Tree.ShowNode(bucket.Children[1])!;
        var anchor = h.Tree.CaptureAnchor();
        bucket.Children.Move(0, 1);
        h.Tree.SetHierarchy(source);
        h.Tree.RestoreAnchor(anchor);
        h.Layout();
        Assert.Same(first, h.Tree.TreeContainerFromItem(bucket.Children[1]));
        Assert.Same(next, h.Tree.TreeContainerFromItem(bucket.Children[0]));
        Assert.True(focused.IsFocused);
        Assert.Same(node, h.Tree.SelectedItem);
        h.Tree.Width = 240;
        h.Layout();
        Assert.InRange(h.Rows.Length, 1, 50);
        Assert.True(focused.IsFocused);
        h.Scroll.Offset = new Vector(0, 10000);
        h.Layout();
        Assert.True(focused.IsFocused);
        Assert.Contains(focused, h.Rows);
        Assert.InRange(h.Rows.Length, 1, 50);
        h.Set([]);
        Assert.Empty(h.Rows);
        Assert.Empty(h.Tree.SelectedItems);
    }

    [AvaloniaFact]
    public void TreeViewportEvictsRootBucketsWithoutLosingCollapseAndNestedNavigation()
    {
        using var h = new Harness();
        var roots = new ObservableCollection<KastnTreeNode>(Enumerable.Range(0, 300).Select(i => Bucket($"bucket-{i}", 2)));
        roots[0].Children.Add(Bucket("nested", 200));
        h.Set(roots);
        h.Tree.ShowNode(roots[0])!.IsExpanded = false;
        h.Layout();
        Assert.False(roots[0].IsExpanded);
        h.Tree.SelectedItem = roots[299].Children[1];
        h.Layout();
        Assert.DoesNotContain(h.Rows, row => ReferenceEquals(row.DataContext, roots[0]));
        var first = h.Tree.ShowNode(roots[0])!;
        Assert.False(first.IsExpanded);
        var nested = roots[0].Children[2];
        var target = nested.Children[199];
        Assert.NotNull(h.Tree.ShowNode(target));
        Assert.True(roots[0].IsExpanded);
        Assert.True(nested.IsExpanded);
        Assert.InRange(h.Rows.Length, 1, 100);
        roots.Move(0, 299);
        h.Tree.SetHierarchy(roots);
        h.Layout();
        Assert.NotNull(h.Tree.ShowNode(target));
        h.Set([Bucket("another-project", 150)]);
        Assert.Null(h.Tree.ShowNode(target));
    }

    private static KastnTreeNode Bucket(string id, int count) => new()
    {
        Id = id, Label = id, Kind = KastnTreeNodeKind.Bucket,
        Children = new(Enumerable.Range(0, count).Select(i => new KastnTreeNode
        { Id = $"{id}-{i}", Label = $"Note {i}", Kind = KastnTreeNodeKind.Slip }))
    };
    private static void Key(Control control, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
    private static void Click(Control control, KeyModifiers modifiers) => control.RaiseEvent(new PointerPressedEventArgs(control,
        new Pointer(0, PointerType.Mouse, true), control, default, 0,
        new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), modifiers));

    private sealed class Harness : IDisposable
    {
        public KastnTreeViewport Tree { get; } = new() { SelectionMode = SelectionMode.Multiple, AutoScrollToSelectedItem = true };
        private readonly Window window;
        public ScrollViewer Scroll => Tree.GetVisualDescendants().OfType<ScrollViewer>().First();
        public TreeViewItem[] Rows => Tree.GetVisualDescendants().OfType<TreeViewItem>().ToArray();
        public Harness()
        {
            Tree.ItemTemplate = new FuncTreeDataTemplate<KastnTreeNode>((node, _) => new TextBlock { Text = node.Label, MinHeight = 28 }, node => node.TreeItems);
            Tree.Styles.Add(new Style(selector => selector.OfType<TreeViewItem>())
            {
                Setters =
                {
                    new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(KastnTreeNode.IsExpanded)) { Mode = BindingMode.TwoWay }),
                    new Setter(ItemsControl.ItemsPanelProperty, new FuncTemplate<Panel?>(() => new KastnTreeItemsPanel()))
                }
            });
            window = new Window { Width = 320, Height = 240, Content = Tree };
            window.Show();
            Layout();
        }
        public void Set(ObservableCollection<KastnTreeNode> nodes) { Tree.SetHierarchy(nodes); Layout(); }
        public void Layout() { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        public void Dispose() { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }
}
