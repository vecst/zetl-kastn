using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace KASTN;

// Flat tree headers have one fixed row height. Keep visible containers by model
// identity across source resets/moves; the extent never depends on sampled sizes.
internal sealed class KastnFlatTreePanel : VirtualizingPanel
{
    internal const double RowHeight = 32;
    private sealed record Row(Control Control, bool Own) { public int Index { get; set; } }
    private readonly Dictionary<object, Row> realized = new(ReferenceEqualityComparer.Instance);
    private object?[] nodes = [];
    private Dictionary<object, int> indexes = new(ReferenceEqualityComparer.Instance);
    private Rect viewport;
    private double width;
    private int scrollTarget = -1;

    public KastnFlatTreePanel() => EffectiveViewportChanged += (_, e) =>
    {
        if (viewport == e.EffectiveViewport) return;
        viewport = e.EffectiveViewport;
        InvalidateMeasure();
    };

    protected override void OnItemsControlChanged(ItemsControl? oldValue)
    {
        if (ItemsControl is not null) OnItemsChanged(Items, new(NotifyCollectionChangedAction.Reset));
        else
        {
            foreach (var row in realized.Values)
                if (!row.Own) oldValue?.ItemContainerGenerator.ClearItemContainer(row.Control);
            realized.Clear();
            indexes.Clear();
            nodes = [];
        }
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        var anchor = realized.Where(pair => pair.Value.Index * RowHeight + RowHeight > viewport.Y
                && pair.Value.Index * RowHeight < viewport.Bottom).OrderBy(pair => pair.Value.Index).FirstOrDefault();
        var oldIndex = anchor.Value?.Index;
        nodes = items.ToArray();
        indexes = new(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < nodes.Length; i++) if (nodes[i] is { } node) indexes[node] = i;
        foreach (var (node, row) in realized.ToArray())
        {
            if (!indexes.TryGetValue(node, out var index)) Retire(node, row);
            else if (row.Index != index)
            {
                ItemContainerGenerator!.ItemContainerIndexChanged(row.Control, row.Index, index);
                row.Index = index;
            }
        }
        if (oldIndex is { } previous && anchor.Key is { } anchored && indexes.TryGetValue(anchored, out var newIndex))
        {
            var top = Math.Max(0, viewport.Y + (newIndex - previous) * RowHeight);
            viewport = new Rect(viewport.X, top, viewport.Width, viewport.Height);
            if (this.FindAncestorOfType<ScrollViewer>() is { } scroll)
                scroll.Offset = new Vector(scroll.Offset.X, top);
        }
        InvalidateMeasure();
    }

    private Row Realize(int index)
    {
        var node = nodes[index]!;
        if (realized.TryGetValue(node, out var row)) return row;
        var generator = ItemContainerGenerator!;
        var needs = generator.NeedsContainer(node, index, out var key);
        var control = needs ? generator.CreateContainer(node, index, key) : (Control)node;
        generator.PrepareItemContainer(control, node, index);
        AddInternalChild(control);
        generator.ItemContainerPrepared(control, node, index);
        row = new(control, !needs) { Index = index };
        realized[node] = row;
        return row;
    }

    private void Retire(object node, Row row)
    {
        realized.Remove(node);
        RemoveInternalChild(row.Control);
        if (!row.Own) ItemContainerGenerator!.ClearItemContainer(row.Control);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        width = availableSize.Width;
        var height = viewport.Height > 0 ? viewport.Height : 512;
        var start = Math.Max(0, (int)Math.Floor((viewport.Y - height / 2) / RowHeight));
        var end = Math.Min(nodes.Length - 1, (int)Math.Ceiling((viewport.Y + height * 1.5) / RowHeight));
        for (var i = start; i <= end; i++) Realize(i);
        if (scrollTarget >= 0 && scrollTarget < nodes.Length) Realize(scrollTarget);
        foreach (var (node, row) in realized.ToArray())
        {
            if ((row.Index < start || row.Index > end) && row.Index != scrollTarget && !row.Control.IsKeyboardFocusWithin)
                Retire(node, row);
            else row.Control.Measure(new(availableSize.Width, RowHeight));
        }
        var ordered = realized.Values.OrderBy(row => row.Index).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var previous = Children.IndexOf(ordered[i].Control);
            if (previous != i) Children.Move(previous, i);
        }
        return new(ordered.Length > 0 ? ordered.Max(row => row.Control.DesiredSize.Width) : 0, nodes.Length * RowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var row in realized.Values)
            row.Control.Arrange(new Rect(0, row.Index * RowHeight, finalSize.Width, RowHeight));
        return finalSize;
    }

    protected override Control? ContainerFromIndex(int index) => index >= 0 && index < nodes.Length && nodes[index] is { } node
        ? realized.GetValueOrDefault(node)?.Control : null;
    protected override int IndexFromContainer(Control container) => realized.Values.FirstOrDefault(row => ReferenceEquals(row.Control, container))?.Index ?? -1;
    protected override IEnumerable<Control> GetRealizedContainers() => realized.Values.OrderBy(row => row.Index).Select(row => row.Control);

    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= nodes.Length || this.GetVisualRoot() is null) return null;
        scrollTarget = index;
        try
        {
            var row = Realize(index);
            row.Control.Measure(new(width, RowHeight));
            row.Control.Arrange(new Rect(0, index * RowHeight, Bounds.Width, RowHeight));
            row.Control.BringIntoView();
            InvalidateMeasure();
            UpdateLayout();
            return row.Control;
        }
        finally { scrollTarget = -1; }
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var index = from is Control control ? IndexFromContainer(control) : -1;
        var next = direction switch
        {
            NavigationDirection.First => 0, NavigationDirection.Last => nodes.Length - 1,
            NavigationDirection.Next or NavigationDirection.Down => index + 1,
            NavigationDirection.Previous or NavigationDirection.Up => index - 1, _ => -1
        };
        if (wrap && nodes.Length > 0) next = (next + nodes.Length) % nodes.Length;
        return ScrollIntoView(next);
    }
}
