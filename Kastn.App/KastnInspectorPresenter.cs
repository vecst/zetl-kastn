using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ZETL.Contracts;

namespace KASTN;

// Owns metadata controls and realized backlink actions. Snapshot queries remain
// in the inspector/index; navigation and opening URLs remain window actions.
internal sealed class KastnInspectorPresenter(StackPanel panel, ScrollViewer scroll) : IDisposable
{
    private sealed class FieldUi
    {
        public required StackPanel Panel { get; init; }
        public required TextBlock Value { get; init; }
        public Button? OpenButton { get; set; }
        public Uri? Uri { get; set; }
    }

    private readonly Dictionary<string, TextBlock> headings = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Section, string Label), FieldUi> fields = [];
    private readonly Dictionary<string, Button> backlinks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KastnViewportItems.Row> rows = new(StringComparer.Ordinal);
    private readonly KastnViewportItems viewport = new() { Spacing = 8 };
    private IReadOnlyList<string> backlinkOrder = [];
    private readonly Dictionary<string, int> backlinkPositions = new(StringComparer.Ordinal);
    private string? projectId;
    private string? slipId;
    private string signature = "";
    private bool active;
    private bool disposed;
    private bool usesViewport;
    private long renderVersion;
    private Action<string> selectSlip = _ => { };
    private Action<Uri> openUrl = _ => { };

    public int RealizedBacklinkCount => backlinks.Count;
    public Button? Backlink(string id) => backlinks.GetValueOrDefault(id);

    public void Suspend(string? currentProjectId, bool hasSlip)
    {
        active = false;
        renderVersion++;
        if (currentProjectId is null || currentProjectId != projectId || !hasSlip) Clear();
    }

    public void Render(KastnProjectIndex? index, ZetlSlipSnapshot? slip,
        Action<string> selectSlip, Action<Uri> openUrl)
    {
        if (disposed) return;
        var sameSelection = index?.Project.Id == projectId && slip?.Id == slipId;
        if (!sameSelection) Clear();
        active = true;
        this.selectSlip = selectSlip;
        this.openUrl = openUrl;
        var nextSignature = $"{index?.Project.Id}|{index?.Project.ChangeSequence}|{index?.Project.MetadataRevision}|{slip?.Id}|{slip?.Revision}";
        if (signature == nextSignature) return;
        var version = ++renderVersion;
        var anchor = usesViewport ? CaptureAnchor() : null;
        var offset = scroll.Offset;
        signature = nextSignature;
        projectId = index?.Project.Id;
        slipId = slip?.Id;
        if (index is null || slip is null)
        {
            panel.Children.Clear();
            panel.Children.Add(new TextBlock
            {
                Text = "No slip is available in the current view.",
                Classes = { "muted" }, TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        var sections = KastnSlipInspector.Build(index, slip);
        var linked = sections.FirstOrDefault(section => section.Heading == "Linked from")?.Fields ?? [];
        backlinkOrder = linked.Select(field => field.Value).ToArray();
        backlinkPositions.Clear();
        for (var i = 0; i < backlinkOrder.Count; i++) backlinkPositions[backlinkOrder[i]] = i;
        var useViewport = linked.Count >= 128;
        if (useViewport != usesViewport)
        {
            viewport.Release();
            backlinks.Clear();
            rows.Clear();
            usesViewport = useViewport;
        }
        var desired = new List<Control>();
        var liveHeadings = new HashSet<string>(StringComparer.Ordinal);
        var liveFields = new HashSet<(string Section, string Label)>();
        var liveBacklinks = new HashSet<string>(StringComparer.Ordinal);
        var desiredRows = new List<KastnViewportItems.Row>();
        foreach (var section in sections)
        {
            liveHeadings.Add(section.Heading);
            if (!headings.TryGetValue(section.Heading, out var heading))
                headings[section.Heading] = heading = new TextBlock
                {
                    Text = section.Heading, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0)
                };
            desired.Add(heading);
            foreach (var field in section.Fields)
            {
                if (section.Heading == "Linked from")
                {
                    var id = field.Value;
                    liveBacklinks.Add(id);
                    if (usesViewport)
                    {
                        if (!rows.TryGetValue(id, out var row)) rows[id] = row = new(id);
                        row.Realize = () => RealizeBacklink(id, field.Label);
                        row.Retire = control =>
                        {
                            if (ReferenceEquals(backlinks.GetValueOrDefault(id), control)) backlinks.Remove(id);
                        };
                        desiredRows.Add(row);
                    }
                    else desired.Add(RealizeBacklink(id, field.Label));
                    continue;
                }
                var key = (section.Heading, field.Label);
                liveFields.Add(key);
                desired.Add(ReconcileField(key, field.Value).Panel);
            }
            if (section.Heading == "Linked from" && usesViewport)
            {
                viewport.SetRows(desiredRows);
                desired.Add(viewport);
            }
        }
        if (linked.Count == 0) viewport.Release();
        foreach (var id in backlinks.Keys.Where(id => !liveBacklinks.Contains(id)).ToArray()) backlinks.Remove(id);
        foreach (var id in rows.Keys.Where(id => !liveBacklinks.Contains(id)).ToArray()) rows.Remove(id);
        foreach (var key in fields.Keys.Where(key => !liveFields.Contains(key)).ToArray()) fields.Remove(key);
        foreach (var key in headings.Keys.Where(key => !liveHeadings.Contains(key)).ToArray()) headings.Remove(key);
        KastnPanelReconciler.SyncChildren(panel.Children, desired);
        scroll.UpdateLayout();
        if (sameSelection)
        {
            RestoreOffset(offset);
            if (anchor is { } saved && rows.ContainsKey(saved.Id))
            {
                RestoreAnchor(saved);
                Dispatcher.UIThread.Post(() =>
                {
                    if (active && !disposed && renderVersion == version) RestoreAnchor(saved);
                }, DispatcherPriority.Background);
            }
        }
    }

    private Button RealizeBacklink(string id, string label)
    {
        if (!backlinks.TryGetValue(id, out var button))
        {
            button = new Button
            {
                Tag = id,
                Padding = new Thickness(9, 3), Margin = new Thickness(0, 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            backlinks[id] = button;
            button.Click += (_, _) =>
            {
                if (active && !disposed && ReferenceEquals(backlinks.GetValueOrDefault(id), button)) selectSlip(id);
            };
            button.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (!active || disposed || !usesViewport || e.Handled || e.Key != Key.Tab
                    || (e.KeyModifiers & ~KeyModifiers.Shift) != 0
                    || !ReferenceEquals(backlinks.GetValueOrDefault(id), button)
                    || !backlinkPositions.TryGetValue(id, out var position)) return;
                var next = position + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                if (next < 0 || next >= backlinkOrder.Count) return;
                e.Handled = true;
                var target = backlinkOrder[next];
                viewport.ShowSlip(target);
                scroll.UpdateLayout();
                backlinks.GetValueOrDefault(target)?.Focus(NavigationMethod.Tab);
            }, RoutingStrategies.Tunnel);
        }
        if (!Equals(button.Content, label)) button.Content = label;
        return button;
    }

    private FieldUi ReconcileField((string Section, string Label) key, string value)
    {
        if (!fields.TryGetValue(key, out var ui))
        {
            var valueText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var fieldPanel = new StackPanel { Spacing = 1 };
            fieldPanel.Children.Add(new TextBlock { Text = key.Label, Classes = { "muted" }, FontSize = 11 });
            fieldPanel.Children.Add(valueText);
            fields[key] = ui = new() { Panel = fieldPanel, Value = valueText };
        }
        if (ui.Value.Text != value)
        {
            ui.Value.Text = value;
            ToolTip.SetTip(ui.Value, value);
        }
        ui.Uri = key.Label == "Original URL" && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri : null;
        if (ui.Uri is not null && ui.OpenButton is null)
        {
            var button = new Button
            {
                Content = "Open URL", Padding = new Thickness(9, 3), Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            ui.OpenButton = button;
            ui.Panel.Children.Add(button);
            button.Click += (_, _) =>
            {
                if (active && !disposed && ReferenceEquals(fields.GetValueOrDefault(key), ui)
                    && ReferenceEquals(ui.OpenButton, button) && ui.Uri is { } current) openUrl(current);
            };
        }
        else if (ui.Uri is null && ui.OpenButton is { } old)
        {
            ui.Panel.Children.Remove(old);
            ui.OpenButton = null;
        }
        return ui;
    }

    private (string Id, double Y)? CaptureAnchor()
    {
        (string Id, double Y)? result = null;
        foreach (var (id, button) in backlinks)
        {
            if (button.TranslatePoint(default, scroll) is not { } point || point.Y + button.Bounds.Height <= 0
                || point.Y >= scroll.Viewport.Height) continue;
            if (result is null || point.Y < result.Value.Y) result = (id, point.Y);
        }
        return result;
    }

    private void RestoreAnchor((string Id, double Y) anchor)
    {
        if (!rows.ContainsKey(anchor.Id)) return;
        viewport.ShowSlip(anchor.Id);
        scroll.UpdateLayout();
        if (backlinks.TryGetValue(anchor.Id, out var button) && button.TranslatePoint(default, scroll) is { } point)
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + point.Y - anchor.Y));
    }

    private void RestoreOffset(Vector offset) => scroll.Offset = new Vector(offset.X,
        Math.Clamp(offset.Y, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));

    public void Clear()
    {
        active = false;
        renderVersion++;
        viewport.Release();
        usesViewport = false;
        backlinks.Clear();
        backlinkOrder = [];
        backlinkPositions.Clear();
        rows.Clear();
        fields.Clear();
        headings.Clear();
        panel.Children.Clear();
        signature = "";
        projectId = null;
        slipId = null;
        scroll.Offset = Vector.Zero;
        selectSlip = _ => { };
        openUrl = _ => { };
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Clear();
    }
}
