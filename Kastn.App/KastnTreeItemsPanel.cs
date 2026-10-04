using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace KASTN;

// A vertical tree items panel that moves realized rows without detaching them.
// VirtualizingPanel supplies the public container-generation hooks; this panel
// deliberately realizes all items, like the tree's original StackPanel.
internal sealed class KastnTreeItemsPanel : VirtualizingPanel
{
    private readonly HashSet<Control> ownContainers = [];

    protected override void OnItemsControlChanged(ItemsControl? oldValue)
    {
        if (ItemsControl is not null)
            OnItemsChanged(Items, new(NotifyCollectionChangedAction.Reset));
        else
            ownContainers.Clear();
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        var generator = ItemContainerGenerator!;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                Add(e.NewStartingIndex, e.NewItems!);
                for (var i = e.NewStartingIndex + e.NewItems!.Count; i < Children.Count; i++)
                    generator.ItemContainerIndexChanged(Children[i], i - e.NewItems.Count, i);
                break;
            case NotifyCollectionChangedAction.Remove:
                Remove(e.OldStartingIndex, e.OldItems!.Count);
                for (var i = e.OldStartingIndex; i < Children.Count; i++)
                    generator.ItemContainerIndexChanged(Children[i], i + e.OldItems.Count, i);
                break;
            case NotifyCollectionChangedAction.Replace when e.OldStartingIndex >= 0 && e.OldItems!.Count == e.NewItems!.Count:
                Remove(e.OldStartingIndex, e.OldItems!.Count);
                Add(e.NewStartingIndex, e.NewItems!);
                break;
            case NotifyCollectionChangedAction.Move when e.OldStartingIndex >= 0 && e.OldItems!.Count == 1:
                Children.Move(e.OldStartingIndex, e.NewStartingIndex);
                for (var i = Math.Min(e.OldStartingIndex, e.NewStartingIndex); i <= Math.Max(e.OldStartingIndex, e.NewStartingIndex); i++)
                {
                    var oldIndex = i == e.NewStartingIndex ? e.OldStartingIndex
                        : e.OldStartingIndex < e.NewStartingIndex ? i + 1 : i - 1;
                    generator.ItemContainerIndexChanged(Children[i], oldIndex, i);
                }
                break;
            default:
                Remove(0, Children.Count);
                Add(0, items.ToArray());
                break;
        }
        InvalidateMeasure();
    }

    private void Add(int index, IEnumerable items)
    {
        var generator = ItemContainerGenerator!;
        foreach (var item in items)
        {
            var needsContainer = generator.NeedsContainer(item, index, out var key);
            var container = needsContainer ? generator.CreateContainer(item, index, key) : (Control)item!;
            if (!needsContainer) ownContainers.Add(container);
            generator.PrepareItemContainer(container, item, index);
            InsertInternalChild(index, container);
            generator.ItemContainerPrepared(container, item, index++);
        }
    }

    private void Remove(int index, int count)
    {
        var generator = ItemContainerGenerator!;
        for (var i = 0; i < count; i++)
        {
            var container = Children[index];
            RemoveInternalChild(container);
            if (!ownContainers.Remove(container)) generator.ClearItemContainer(container);
        }
    }

    protected override Control? ContainerFromIndex(int index) =>
        index >= 0 && index < Children.Count ? Children[index] : null;

    protected override int IndexFromContainer(Control container) => Children.IndexOf(container);
    protected override IEnumerable<Control> GetRealizedContainers() => Children;

    protected override Control? ScrollIntoView(int index)
    {
        var container = ContainerFromIndex(index);
        container?.BringIntoView();
        return container;
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        if (Children.Count == 0) return null;
        var index = from is Control control ? Children.IndexOf(control) : -1;
        if (index < 0 && direction is not NavigationDirection.First and not NavigationDirection.Last) return null;
        var next = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => Children.Count - 1,
            NavigationDirection.Next or NavigationDirection.Down => index + 1,
            NavigationDirection.Previous or NavigationDirection.Up => index - 1,
            _ => -1
        };
        if (wrap && (direction is NavigationDirection.Next or NavigationDirection.Down) && next == Children.Count) next = 0;
        if (wrap && (direction is NavigationDirection.Previous or NavigationDirection.Up) && next == -1) next = Children.Count - 1;
        return ContainerFromIndex(next);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0d;
        var height = 0d;
        foreach (var child in Children)
        {
            child.Measure(new(availableSize.Width, double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
        }
        return new(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0d;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            child.Arrange(new Rect(0, y, Math.Max(finalSize.Width, child.DesiredSize.Width), child.DesiredSize.Height));
            y += child.DesiredSize.Height;
        }
        return finalSize;
    }
}
