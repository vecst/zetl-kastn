using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace KASTN;

// Lightweight rows remain in the document; Avalonia realizes their variable-height
// controls only near the viewport and supplies scrolling and scroll anchoring.
internal sealed class KastnReaderItems : ItemsControl
{
    internal sealed class Row(string id)
    {
        public string Id { get; } = id;
        public Func<Border> Realize { get; set; } = null!;
        public Action<Border> Retire { get; set; } = null!;
    }

    private IReadOnlyList<Row> rows = [];
    public double Spacing { get; set; }

    public KastnReaderItems()
    {
        ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel { CacheLength = 0.5 });
    }

    protected override Type StyleKeyOverride => typeof(ItemsControl);

    public bool SetRows(IReadOnlyList<Row> desired)
    {
        if (!rows.SequenceEqual(desired))
        {
            Release();
            rows = desired;
            ItemsSource = rows;
            return true;
        }
        else
        {
            foreach (var container in GetRealizedContainers().OfType<Border>())
                PrepareRow(container, (Row)container.Tag!, IndexFromContainer(container));
        }
        return false;
    }

    public void Release()
    {
        foreach (var container in GetRealizedContainers().OfType<Border>().ToArray())
            ClearContainerForItemOverride(container);
        ItemsSource = null;
        rows = [];
    }

    public bool ShowSlip(string id)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Id == id)
            {
                // Relative height estimates after a distant jump can leave a
                // leading gap. Enter the group's actual origin before item zero.
                if (i == 0)
                {
                    this.BringIntoView(new Rect(0, 0, Bounds.Width, 1));
                    UpdateLayout();
                }
                ScrollIntoView(i);
                return true;
            }
        return false;
    }

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        // Empty shells must not retain evicted note controls or picture loads.
        recycleKey = null;
        return true;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new Border();

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index) =>
        PrepareRow((Border)container, (Row)item!, index);

    private void PrepareRow(Border container, Row row, int index)
    {
        container.Tag = row;
        container.Margin = new Thickness(0, 0, 0, index < rows.Count - 1 ? Spacing : 0);
        container.Child = row.Realize();
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        var border = (Border)container;
        if (border.Tag is Row row && border.Child is Border block) row.Retire(block);
        border.Child = null;
        border.Tag = null;
    }
}
