using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal sealed record KastnReaderAppearance(IBrush? BorderBrush, IBrush? AccentBrush, IBrush? SurfaceBrush);
internal sealed record KastnReaderRenderInputs(KastnProjectIndex Index, IReadOnlyList<ZetlSlipSnapshot> Slips,
    ZetlViewDocument View, KastnViewRenderKey Key, bool DeletedOnly,
    KastnSlipContentRenderer ContentRenderer, KastnReaderAppearance Appearance, Action<string> SelectSlip);

// Owns the reader's live controls and reconciliation. The shared picture cache
// owns bitmaps; each load captures a project and may assign only to its live block.
internal sealed class KastnReaderPresenter(StackPanel panel, ScrollViewer scroll, KastnPictureCache pictures) : IDisposable
{
    // Keep the simpler, identity-preserving reconciliation for small documents.
    private const int ViewportThreshold = 128;
    private sealed record ReaderBlock(string Id, Border Block, string RenderKey, KastnRenderedSlipContent? Content,
        Image? Image, TextBlock? PictureStatus)
    {
        public Task? PictureLoad { get; set; }
    }
    private readonly Dictionary<string, ReaderBlock> slipCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (TextBlock Heading, string RenderKey)> headingCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Border Box, StackPanel Content)> groupCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> blocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KastnViewportItems> viewportGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KastnViewportItems.Row> viewportRows = new(StringComparer.Ordinal);
    private bool usesViewport;
    private bool viewportStructureChanged;
    public IReadOnlyDictionary<string, Border> Blocks => blocks;
    private KastnViewRenderKey? renderKey;
    private string? projectId;
    private string? highlightedSlipId;
    private bool deletedOnly;
    private bool active;
    private bool disposed;
    private long selectionVersion;
    private KastnReaderAppearance appearance = new(null, null, null);

    public void Render(KastnReaderRenderInputs inputs, string? selectedSlipId, bool force = false)
    {
        if (disposed) return;
        var sameProject = projectId == inputs.Index.Project.Id;
        if (!sameProject) Clear();
        projectId = inputs.Index.Project.Id;
        active = true;
        appearance = inputs.Appearance;
        var savedOffset = scroll.Offset;
        var anchor = sameProject && usesViewport ? CaptureViewportAnchor() : null;
        var rebuilt = force || renderKey != inputs.Key || deletedOnly != inputs.DeletedOnly;
        if (rebuilt)
        {
            BuildDocument(inputs);
            renderKey = inputs.Key;
            deletedOnly = inputs.DeletedOnly;
        }
        UpdateSelection(selectedSlipId, scrollIntoView: !rebuilt);
        if (rebuilt && sameProject)
        {
            RestoreScroll(savedOffset);
            if (usesViewport && viewportStructureChanged && anchor is { } savedAnchor)
                RestoreViewportAnchor(savedAnchor);
        }
    }

    public void Suspend()
    {
        active = false;
        selectionVersion++;
    }

    public void Clear()
    {
        Suspend();
        renderKey = null;
        projectId = null;
        highlightedSlipId = null;
        ClearViewport();
        usesViewport = false;
        viewportStructureChanged = false;
        panel.Children.Clear();
        blocks.Clear();
        slipCache.Clear();
        headingCache.Clear();
        groupCache.Clear();
        scroll.Offset = Vector.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Clear();
    }

    private void BuildDocument(KastnReaderRenderInputs inputs)
    {
        viewportStructureChanged = false;
        var project = inputs.Index.Project;
        var visible = inputs.Slips;
        blocks.Clear();
        var groups = ZetlViewRenderer.BuildGroups(project, visible, inputs.View);
        var useViewport = groups.Sum(group => group.Slips.Count) >= ViewportThreshold;
        if (useViewport != usesViewport)
        {
            ClearViewport();
            slipCache.Clear();
            usesViewport = useViewport;
        }
        if (groups.Count == 0)
        {
            slipCache.Clear();
            headingCache.Clear();
            groupCache.Clear();
            highlightedSlipId = null;
            KastnPanelReconciler.SyncChildren(panel.Children,
            [
                new TextBlock
                {
                    Text = visible.Count == 0
                        ? "No slips match the current filters."
                        : "Every slip in view is hidden from views.",
                    Classes = { "muted" },
                    TextWrapping = TextWrapping.Wrap
                }
            ]);
            return;
        }

        var preferSlipKindOverBucketKind = inputs.Key.PreferSlipKindOverBucketKind;
        var contentRenderer = inputs.ContentRenderer;
        var renderedSlipIds = new HashSet<string>(StringComparer.Ordinal);
        var renderedGroupKeys = new HashSet<string>(StringComparer.Ordinal);
        var desiredChildren = new List<Control>();
        foreach (var group in groups)
        {
            // A container bucket ("group") wraps its heading and slips in a bordered box;
            // otherwise they go straight into the document.
            var isGroup = group.RenderKind == ZetlBucketRenderKinds.Group;
            var groupKey = group.HeaderBucket?.Id ?? $"heading:{group.Heading}";
            renderedGroupKeys.Add(groupKey);

            var headingText = ZetlViewRenderer.HeadingText(group, inputs.View);
            var headingKey =
                $"{headingText}|{group.EffectiveLevel}|{group.HeadingBold}|{group.HeadingAlign}|{group.Depth}|{isGroup}";
            if (!headingCache.TryGetValue(groupKey, out var heading)
                || !string.Equals(heading.RenderKey, headingKey, StringComparison.Ordinal))
            {
                heading = (new TextBlock
                {
                    Text = headingText,
                    FontSize = Math.Max(14, 27 - (3 * group.EffectiveLevel)),
                    FontWeight = group.HeadingBold ? FontWeight.Bold : FontWeight.SemiBold,
                    TextAlignment = ZetlViewRenderer.NormalizeHeadingAlign(group.HeadingAlign) switch
                    {
                        "center" => TextAlignment.Center,
                        "right" => TextAlignment.Right,
                        _ => TextAlignment.Left,
                    },
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(isGroup ? 0 : group.Depth * 14, isGroup ? 0 : 8, 0, 2)
                }, headingKey);
                headingCache[groupKey] = heading;
            }

            var groupChildren = isGroup ? new List<Control>() : desiredChildren;
            groupChildren.Add(heading.Heading);
            var rows = useViewport ? new List<KastnViewportItems.Row>() : null;

            // Each note carries its own list kind (authoritative, not a view-wide
            // style); ordered notes count up over their run and any non-ordered note
            // or picture restarts it.
            var orderedRun = 0;
            foreach (var slip in group.Slips)
            {
                var listKinds = ZetlViewRenderer.ResolveListKinds(
                    project, slip, preferSlipKindOverBucketKind);
                var marker = KastnSlipContentRenderer.OuterListMarker(listKinds.Outer, slip.Checked, ref orderedRun)
                    + KastnSlipContentRenderer.InnerListMarker(listKinds.Inner, slip.Checked);
                var checkable = listKinds.IsCheckable;

                var depth = isGroup ? 0 : group.Depth;
                var renderKey = $"{slip.Revision}|{depth}|{marker}|{checkable}";
                Border Realize()
                {
                    if (!slipCache.TryGetValue(slip.Id, out var cached)
                        || !string.Equals(cached.RenderKey, renderKey, StringComparison.Ordinal)
                        || cached.Content?.MatchesLinks(inputs.Index) == false)
                    {
                        cached = BuildSlipBlock(slip, depth, marker, contentRenderer, renderKey, inputs.SelectSlip, checkable);
                        slipCache[slip.Id] = cached;
                        if (cached.Image is not null) cached.PictureLoad = LoadPictureAsync(project.Id, slip, cached);
                    }
                    blocks[slip.Id] = cached.Block;
                    ApplyHighlight(cached.Block, highlightedSlipId == slip.Id);
                    return cached.Block;
                }
                renderedSlipIds.Add(slip.Id);
                if (rows is not null)
                {
                    if (!viewportRows.TryGetValue(slip.Id, out var row))
                        viewportRows[slip.Id] = row = new(slip.Id);
                    row.Realize = Realize;
                    row.Retire = block => RetireBlock(slip.Id, (Border)block);
                    rows.Add(row);
                }
                else groupChildren.Add(Realize());
            }

            if (rows is not null)
            {
                if (!viewportGroups.TryGetValue(groupKey, out var items))
                    viewportGroups[groupKey] = items = new();
                items.Spacing = isGroup ? 2 : panel.Spacing;
                viewportStructureChanged |= items.SetRows(rows);
                groupChildren.Add(items);
            }

            if (isGroup)
            {
                if (!groupCache.TryGetValue(groupKey, out var box))
                {
                    var content = new StackPanel { Spacing = 2 };
                    box = (new Border
                    {
                        BorderThickness = new Avalonia.Thickness(1),
                        BorderBrush = inputs.Appearance.BorderBrush ?? Brushes.Gray,
                        CornerRadius = new Avalonia.CornerRadius(6),
                        Padding = new Avalonia.Thickness(12, 8),
                        Child = content
                    }, content);
                    groupCache[groupKey] = box;
                }

                box.Box.Margin = new Avalonia.Thickness(group.Depth * 14, 10, 0, 4);
                KastnPanelReconciler.SyncChildren(box.Content.Children, groupChildren);
                desiredChildren.Add(box.Box);
            }
        }

        KastnPanelReconciler.SyncChildren(panel.Children, desiredChildren);

        foreach (var staleKey in viewportGroups.Keys.Where(key => !renderedGroupKeys.Contains(key)).ToArray())
        {
            viewportGroups[staleKey].Release();
            viewportGroups.Remove(staleKey);
        }
        foreach (var staleId in viewportRows.Keys.Where(id => !renderedSlipIds.Contains(id)).ToArray())
            viewportRows.Remove(staleId);

        foreach (var staleId in slipCache.Keys.Where(id => !renderedSlipIds.Contains(id)).ToList())
        {
            slipCache.Remove(staleId);
        }

        foreach (var staleKey in headingCache.Keys.Where(key => !renderedGroupKeys.Contains(key)).ToList())
        {
            headingCache.Remove(staleKey);
        }

        foreach (var staleKey in groupCache.Keys.Where(key => !renderedGroupKeys.Contains(key)).ToList())
        {
            groupCache.Remove(staleKey);
        }
    }

    private void RetireBlock(string id, Border block)
    {
        if (slipCache.TryGetValue(id, out var current) && ReferenceEquals(current.Block, block))
        {
            slipCache.Remove(id);
            blocks.Remove(id);
        }
    }

    private void ClearViewport()
    {
        foreach (var items in viewportGroups.Values) items.Release();
        viewportGroups.Clear();
        viewportRows.Clear();
    }

    private ReaderBlock BuildSlipBlock(
        ZetlSlipSnapshot slip, int depth, string marker, KastnSlipContentRenderer renderer,
        string renderKey, Action<string> selectSlip, bool checkable)
    {
        ReaderBlock? result = null;
        renderer = renderer.WithActionGuard(() => active && result is not null && IsLive(result));
        KastnRenderedSlipContent? renderedContent = null;
        Image? pictureImage = null;
        TextBlock? pictureStatus = null;
        StackPanel content;
        if (slip.Type == ZetlSlipType.Picture)
        {
            content = new StackPanel { Spacing = 5 };
            var image = new Avalonia.Controls.Image
            {
                Stretch = Stretch.Uniform,
                MaxHeight = 520,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var loading = new TextBlock
            {
                Text = "Loading picture…",
                Classes = { "muted" },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var preview = new Grid
            {
                MinHeight = 150,
                Children = { image, loading }
            };
            content.Children.Add(new Border
            {
                Classes = { "surface" },
                Padding = new Avalonia.Thickness(8),
                Child = preview
            });
            KastnSlipContentRenderer.AddPictureCaption(content, slip);

            if (slip.Picture is { } picture && pictures.FindDecoded(picture.Sha256, 1100) is { } cachedBitmap)
            {
                image.Source = cachedBitmap;
                loading.IsVisible = false;
            }
            else
            {
                pictureImage = image;
                pictureStatus = loading;
            }
        }
        else
        {
            renderedContent = renderer.CreateTextContent(slip);
            content = renderedContent.Panel;
        }

        var child = renderer.WithListMarker(content, slip, marker, checkable);

        var block = new Border
        {
            Tag = slip.Id,
            Child = child,
            Padding = new Avalonia.Thickness(8, 6),
            Margin = new Avalonia.Thickness((depth + 1) * 14, 0, 0, 4),
            CornerRadius = new Avalonia.CornerRadius(4),
            BorderThickness = new Avalonia.Thickness(1),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        result = new ReaderBlock(slip.Id, block, renderKey, renderedContent, pictureImage, pictureStatus);
        block.PointerPressed += (_, args) =>
        {
            if (!args.Handled && active && IsLive(result)) selectSlip(slip.Id);
        };
        return result;
    }

    private bool IsLive(ReaderBlock block) => !disposed
        && slipCache.TryGetValue(block.Id, out var current) && ReferenceEquals(current, block);

    private async Task LoadPictureAsync(string capturedProjectId, ZetlSlipSnapshot slip, ReaderBlock block)
    {
        bool IsCurrent() => projectId == capturedProjectId && IsLive(block);
        try
        {
            var content = await pictures.GetContentAsync(capturedProjectId, slip);
            if (!IsCurrent()) return;
            if (content is null)
            {
                block.PictureStatus!.Text = "Picture unavailable.";
                return;
            }
            var bitmap = pictures.Decode(content, 1100);
            if (!IsCurrent()) return;
            block.Image!.Source = bitmap;
            block.PictureStatus!.IsVisible = false;
        }
        catch (Exception ex) when (KastnPictureCache.IsLoadFailure(ex))
        {
            if (IsCurrent()) block.PictureStatus!.Text = "Picture unavailable.";
        }
    }

    public void UpdateSelection(string? selectedSlipId, bool scrollIntoView = true)
    {
        if (disposed || !active) return;
        var version = ++selectionVersion;
        if (highlightedSlipId is not null && blocks.TryGetValue(highlightedSlipId, out var previous))
            ApplyHighlight(previous, false);
        highlightedSlipId = selectedSlipId;
        if (selectedSlipId is null) return;
        if (usesViewport && scrollIntoView)
        {
            scroll.UpdateLayout();
            ShowViewportSlip(selectedSlipId);
            Dispatcher.UIThread.Post(() =>
            {
                if (active && selectionVersion == version && highlightedSlipId == selectedSlipId)
                    ShowViewportSlip(selectedSlipId);
            }, DispatcherPriority.Background);
        }
        if (!slipCache.TryGetValue(selectedSlipId, out var selected)) return;
        ApplyHighlight(selected.Block, true);
        highlightedSlipId = selectedSlipId;
        if (!scrollIntoView || usesViewport) return;
        selected.Block.BringIntoView();
        Dispatcher.UIThread.Post(() =>
        {
            if (active && selectionVersion == version && highlightedSlipId == selectedSlipId && IsLive(selected))
                selected.Block.BringIntoView();
        }, DispatcherPriority.Background);
    }

    private void ShowViewportSlip(string id)
    {
        foreach (var items in viewportGroups.Values)
            if (items.ShowSlip(id)) break;
    }

    private (string Id, double Y)? CaptureViewportAnchor()
    {
        (string Id, double Y)? result = null;
        foreach (var (id, block) in blocks)
        {
            if (block.TranslatePoint(default, scroll) is not { } point
                || point.Y + block.Bounds.Height <= 0 || point.Y >= scroll.Viewport.Height) continue;
            if (result is null || point.Y < result.Value.Y) result = (id, point.Y);
        }
        return result;
    }

    private void RestoreViewportAnchor((string Id, double Y) anchor)
    {
        if (!viewportRows.ContainsKey(anchor.Id)) return;
        ShowViewportSlip(anchor.Id);
        scroll.UpdateLayout();
        if (blocks.TryGetValue(anchor.Id, out var block) && block.TranslatePoint(default, scroll) is { } point)
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + point.Y - anchor.Y));
    }

    private void ApplyHighlight(Border block, bool on)
    {
        block.BorderBrush = on ? appearance.AccentBrush ?? Brushes.Transparent : Brushes.Transparent;
        block.Background = on ? appearance.SurfaceBrush ?? Brushes.Transparent : Brushes.Transparent;
    }

    // Settle layout before restoring the offset, so the reader never paints a
    // temporary reset to the top. A shorter document clamps to its new extent.
    private void RestoreScroll(Vector savedOffset)
    {
        scroll.UpdateLayout();
        var maxY = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        scroll.Offset = new Vector(savedOffset.X, Math.Min(savedOffset.Y, maxY));
    }
}
