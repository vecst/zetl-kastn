using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    private bool lastBoardModeActive;

    private void RefreshViewer()
    {
        if (currentProject is null)
        {
            viewerSummaryText.Text = "No project selected.";
            lastRenderedViewText = "";
            lastViewSignature = "";
            ClearViewDocument();
            RefreshSlipInspector([]);
            copyViewButton.IsEnabled = false;
            exportViewButton.IsEnabled = false;
            formatFidelityNote.IsVisible = false;
            return;
        }

        var visible = CurrentViewSlips();
        RefreshSlipInspector(visible);
        viewerSummaryText.Text = visible.Count == 0
            ? "No slips match the current filters."
            : $"{visible.Count} of {currentProject.Slips.Count} slips in the current view.";

        // The on-screen View is always the readable per-slip document; the view kind
        // governs only the Copy/Export artifact. Rebuild only when the rendered content
        // could have changed (a project mutation, the filters, or the chosen view) — a
        // pure selection change just re-highlights, so picture blocks never reload.
        var signature = ViewSignature(visible);
        var rebuilt = signature != lastViewSignature || viewSlipBlocks.Count == 0 || boardModeActive != lastBoardModeActive;
        var sameProject = string.Equals(lastViewerProjectId, currentProject.Id, StringComparison.Ordinal);
        lastViewerProjectId = currentProject.Id;
        var savedOffset = viewerDocumentScroll.Offset;
        if (rebuilt)
        {
            if (boardModeActive)
            {
                BuildBoardView(visible);
            }
            else
            {
                BuildViewDocument(visible);
            }
            lastViewSignature = signature;
            lastBoardModeActive = boardModeActive;
        }

        // A rebuild resets the document scroll to the top. For an in-place change to
        // the same project (e.g. hiding a slip), preserve the reader's position
        // instead of flashing to the top and snapping back; only a pure selection
        // change (no rebuild) scrolls the selected block into view.
        UpdateViewSelectionHighlight(scrollIntoView: !rebuilt);
        if (rebuilt && sameProject)
        {
            RestoreViewScroll(savedOffset);
        }

        if (SelectedView.Kind == ZetlViewKinds.Pdf)
        {
            // PDF is binary — there is no text to copy; Export writes the .pdf.
            lastRenderedViewText = "";
            copyViewButton.IsEnabled = false;
            exportViewButton.IsEnabled = visible.Count > 0;
        }
        else
        {
            lastRenderedViewText = visible.Count == 0
                ? ""
                : ZetlViewRenderer.Render(
                    currentProject,
                    visible,
                    SelectedView,
                    preferSlipKindOverBucketKind: CurrentAppSettings().KastnPreferSlipKindOverBucketKind);
            var hasOutput = lastRenderedViewText.Length > 0;
            copyViewButton.IsEnabled = hasOutput;
            exportViewButton.IsEnabled = hasOutput;
        }

        deleteViewMenuItem.IsEnabled = !ZetlViewDefaults.IsBuiltIn(SelectedView.Id);
        UpdateFormatFidelityNote();
    }

    // The on-screen reader always renders slip formatting and alignment; the selected
    // view kind only decides what its Copy/Export artifact carries. Tell the editor
    // when that artifact would drop the formatting the reader is showing, so a user
    // authoring toward, say, a TSV export is not surprised by raw markup in the output.
    private void UpdateFormatFidelityNote()
    {
        var kind = SelectedView.Kind;
        var preservesFormatting = ZetlViewRenderer.ExportPreservesFormatting(kind);
        var preservesAlignment = ZetlViewRenderer.ExportPreservesAlignment(kind);
        if (preservesFormatting && preservesAlignment)
        {
            formatFidelityNote.IsVisible = false;
            return;
        }

        formatFidelityNote.Text = preservesFormatting
            // Markdown carries inline formatting but has no alignment syntax.
            ? "Markdown export keeps text formatting but not block alignment."
            : $"The {kind} view copies and exports as literal text — formatting shows "
                + "here in the reader but is dropped from Copy/Export.";
        formatFidelityNote.IsVisible = true;
    }

    private string ViewSignature(IReadOnlyList<ZetlSlipSnapshot> visible)
    {
        // Bumps on any durable mutation (ChangeSequence), filter change (the id set),
        // or chosen view — the cases where the rendered document actually differs.
        return $"{SelectedView.Id}|{currentProject?.ChangeSequence}|"
            + string.Join(',', visible.Select(slip => slip.Id));
    }

    private void RefreshSlipInspector(IReadOnlyList<ZetlSlipSnapshot> visible)
    {
        // The inspector follows the tree selection, which can be any slip in the
        // project — including a deleted or filtered-out one the whole-project View
        // does not render. Keep it as long as the slip still exists in the project.
        _ = visible;
        var selected = SelectedTreeNode?.Slip;
        if (selected is not null
            && currentProject?.Slips.All(slip => slip.Id != selected.Id) != false)
        {
            selected = null;
        }

        inspectedSlipId = selected?.Id;
        RenderSlipInspector(selected);
    }

    private void InspectSlip(string slipId)
    {
        inspectedSlipId = slipId;
        RenderSlipInspector(currentProject?.Slips.FirstOrDefault(slip => slip.Id == slipId));
    }

    private void RenderSlipInspector(ZetlSlipSnapshot? slip)
    {
        slipInspectorFieldsPanel.Children.Clear();
        if (currentProject is null || slip is null)
        {
            slipInspectorFieldsPanel.Children.Add(new TextBlock
            {
                Text = "No slip is available in the current view.",
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var section in KastnSlipInspector.Build(currentProject, slip))
        {
            slipInspectorFieldsPanel.Children.Add(new TextBlock
            {
                Text = section.Heading,
                FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 8, 0, 0)
            });
            if (string.Equals(section.Heading, "Linked from", StringComparison.Ordinal))
            {
                foreach (var field in section.Fields)
                {
                    var targetSlipId = field.Value;
                    var navigateButton = new Button
                    {
                        Content = field.Label,
                        Padding = new Avalonia.Thickness(9, 3),
                        Margin = new Avalonia.Thickness(0, 2, 0, 2),
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    navigateButton.Click += (_, _) =>
                    {
                        var node = FindTreeNode(projectTree.ItemsSource as IEnumerable<KastnTreeNode>, targetSlipId);
                        if (node is not null && !ReferenceEquals(projectTree.SelectedItem, node))
                        {
                            projectTree.SelectedItem = node;
                        }
                    };
                    slipInspectorFieldsPanel.Children.Add(navigateButton);
                }
                continue;
            }
            foreach (var field in section.Fields)
            {
                var fieldPanel = new StackPanel { Spacing = 1 };
                fieldPanel.Children.Add(new TextBlock
                {
                    Text = field.Label,
                    Classes = { "muted" },
                    FontSize = 11
                });
                fieldPanel.Children.Add(new TextBlock
                {
                    Text = field.Value,
                    TextWrapping = TextWrapping.Wrap,
                    [ToolTip.TipProperty] = field.Value
                });
                if (string.Equals(field.Label, "Original URL", StringComparison.Ordinal)
                    && Uri.TryCreate(field.Value, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    var openButton = new Button
                    {
                        Content = "Open URL",
                        Padding = new Avalonia.Thickness(9, 3),
                        Margin = new Avalonia.Thickness(0, 4, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    openButton.Click += (_, _) => OpenInspectorUrl(uri);
                    fieldPanel.Children.Add(openButton);
                }

                slipInspectorFieldsPanel.Children.Add(fieldPanel);
            }
        }
    }

    private void OpenInspectorUrl(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            statusText.Text = "Kastn could not open that URL.";
        }
    }

    // Build the whole-project readable document: one addressable block per slip,
    // grouped/nested by the chosen view's BuildGroups (which drops excluded slips).
    // Reusable per-slip view blocks, group headings, and group boxes: each is
    // rebuilt only when its render inputs change, and the document panel is
    // reconciled with minimal moves instead of cleared — so the refresh that
    // follows every mutation touches only the affected controls. A full
    // clear-and-re-add re-measured every block and cost hundreds of ms per
    // action on a few-hundred-slip project.
    private readonly Dictionary<string, (Border Block, string RenderKey)> viewSlipBlockCache =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, (TextBlock Heading, string RenderKey)> viewHeadingCache =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Border Box, StackPanel Content)> viewGroupBoxCache =
        new(StringComparer.Ordinal);

    private void BuildViewDocument(IReadOnlyList<ZetlSlipSnapshot> visible)
    {
        var generation = ++pictureRenderGeneration;
        viewSlipBlocks.Clear();
        if (currentProject is null)
        {
            ClearViewDocument();
            return;
        }

        var groups = ZetlViewRenderer.BuildGroups(currentProject, visible, SelectedView);
        if (groups.Count == 0)
        {
            SyncPanelChildren(viewerDocumentPanel.Children,
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

        var preferSlipKindOverBucketKind = CurrentAppSettings().KastnPreferSlipKindOverBucketKind;
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

            var headingText = ZetlViewRenderer.HeadingText(group, SelectedView);
            var headingKey =
                $"{headingText}|{group.EffectiveLevel}|{group.HeadingBold}|{group.HeadingAlign}|{group.Depth}|{isGroup}";
            if (!viewHeadingCache.TryGetValue(groupKey, out var heading)
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
                viewHeadingCache[groupKey] = heading;
            }

            var groupChildren = isGroup ? new List<Control>() : desiredChildren;
            groupChildren.Add(heading.Heading);

            // Each note carries its own list kind (authoritative, not a view-wide
            // style); ordered notes count up over their run and any non-ordered note
            // or picture restarts it.
            var orderedRun = 0;
            foreach (var slip in group.Slips)
            {
                var slipKind = slip.Type == ZetlSlipType.Picture
                    ? ""
                    : ZetlViewRenderer.SlipBlockKind(slip);
                var bucketListKind = !slip.IgnoreBucketRenderKind
                    && !(preferSlipKindOverBucketKind && slipKind.Length > 0)
                    && group.RenderKind is ZetlBucketRenderKinds.Bullet
                    or ZetlBucketRenderKinds.Ordered
                    or ZetlBucketRenderKinds.Task
                        ? group.RenderKind
                        : "";
                var markerKind = bucketListKind.Length > 0
                    ? bucketListKind
                    : ZetlViewRenderer.IsListRenderKind(slipKind) ? slipKind : "";
                var innerKind = bucketListKind.Length > 0
                    && ZetlViewRenderer.IsListRenderKind(slipKind)
                    && !string.Equals(slipKind, bucketListKind, StringComparison.Ordinal)
                        ? slipKind
                        : "";
                var marker = ViewOuterListMarker(markerKind, slip.Checked, ref orderedRun)
                    + ViewInnerListMarker(innerKind, slip.Checked);
                var checkable = markerKind == ZetlBlockKinds.Task || innerKind == ZetlBlockKinds.Task;

                var depth = isGroup ? 0 : group.Depth;
                var renderKey = $"{slip.Revision}|{depth}|{marker}|{checkable}";
                if (!viewSlipBlockCache.TryGetValue(slip.Id, out var cached)
                    || !string.Equals(cached.RenderKey, renderKey, StringComparison.Ordinal))
                {
                    cached = (BuildSlipBlock(slip, depth, generation, marker, checkable: checkable), renderKey);
                    viewSlipBlockCache[slip.Id] = cached;
                }

                viewSlipBlocks[slip.Id] = cached.Block;
                renderedSlipIds.Add(slip.Id);
                groupChildren.Add(cached.Block);
            }

            if (isGroup)
            {
                if (!viewGroupBoxCache.TryGetValue(groupKey, out var box))
                {
                    var content = new StackPanel { Spacing = 2 };
                    box = (new Border
                    {
                        BorderThickness = new Avalonia.Thickness(1),
                        BorderBrush = ThemeBrush("ZetlBorderBrush") ?? Brushes.Gray,
                        CornerRadius = new Avalonia.CornerRadius(6),
                        Padding = new Avalonia.Thickness(12, 8),
                        Child = content
                    }, content);
                    viewGroupBoxCache[groupKey] = box;
                }

                box.Box.Margin = new Avalonia.Thickness(group.Depth * 14, 10, 0, 4);
                SyncPanelChildren(box.Content.Children, groupChildren);
                desiredChildren.Add(box.Box);
            }
        }

        SyncPanelChildren(viewerDocumentPanel.Children, desiredChildren);

        foreach (var staleId in viewSlipBlockCache.Keys.Where(id => !renderedSlipIds.Contains(id)).ToList())
        {
            viewSlipBlockCache.Remove(staleId);
        }

        foreach (var staleKey in viewHeadingCache.Keys.Where(key => !renderedGroupKeys.Contains(key)).ToList())
        {
            viewHeadingCache.Remove(staleKey);
        }

        foreach (var staleKey in viewGroupBoxCache.Keys.Where(key => !renderedGroupKeys.Contains(key)).ToList())
        {
            viewGroupBoxCache.Remove(staleKey);
        }
    }

    private static string ViewOuterListMarker(string kind, bool isChecked, ref int orderedRun)
    {
        if (kind == ZetlBlockKinds.Ordered)
        {
            return $"{++orderedRun}.";
        }

        orderedRun = 0;
        return kind switch
        {
            ZetlBlockKinds.Task => isChecked ? "☑" : "☐",
            ZetlBlockKinds.Bullet => "•",
            _ => ""
        };
    }

    private static string ViewInnerListMarker(string kind, bool isChecked) => kind switch
    {
        ZetlBlockKinds.Task => isChecked ? " ☑" : " ☐",
        ZetlBlockKinds.Bullet => " •",
        ZetlBlockKinds.Ordered => " 1.",
        _ => ""
    };

    private Border BuildSlipBlock(
        ZetlSlipSnapshot slip, int depth, int generation, string marker, bool checkable = false)
    {
        var content = new StackPanel { Spacing = 5 };
        if (slip.Type == ZetlSlipType.Picture)
        {
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
            var caption = string.IsNullOrWhiteSpace(slip.Text) ? slip.Title : slip.Text;
            if (!string.IsNullOrWhiteSpace(caption))
            {
                content.Children.Add(new TextBlock
                {
                    Text = caption.Trim(),
                    Classes = { "muted" },
                    FontStyle = FontStyle.Italic,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            if (CachedDecodedPicture(slip, 1100) is { } cachedBitmap)
            {
                image.Source = cachedBitmap;
                loading.IsVisible = false;
            }
            else
            {
                _ = LoadPicturePreviewAsync(slip, image, loading, generation);
            }
        }
        else
        {
            var text = string.IsNullOrWhiteSpace(slip.Text) ? slip.Title : slip.Text;
            AppendSlipBlocks(content, slip, text.Trim());
        }

        // The view's list style puts a marker beside each slip (the slip's content may
        // still hold its own item-7 lists). A hanging-indent grid keeps wraps aligned.
        Control child = content;
        if (marker.Length > 0)
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 6
            };
            var markerBlock = new TextBlock
            {
                Text = marker,
                MinWidth = 18,
                VerticalAlignment = VerticalAlignment.Top
            };
            if (checkable)
            {
                // The task checkbox toggles the note's checked property in place; mark
                // the event handled so it does not also fall through to slip selection.
                markerBlock.Cursor = new Cursor(StandardCursorType.Hand);
                var slipId = slip.Id;
                markerBlock.PointerPressed += async (_, args) =>
                {
                    args.Handled = true;
                    await ToggleSlipCheckedAsync(slipId);
                };
            }

            Grid.SetColumn(markerBlock, 0);
            Grid.SetColumn(content, 1);
            grid.Children.Add(markerBlock);
            grid.Children.Add(content);
            child = grid;
        }

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
        block.PointerPressed += OnViewSlipBlockPressed;
        viewSlipBlocks[slip.Id] = block;
        return block;
    }

    // A block click selects that slip in the tree, which drives the editor, inspector,
    // and (back through RefreshViewer) the View highlight — the View ⇄ tree bridge.
    private void OnViewSlipBlockPressed(object? sender, PointerPressedEventArgs args)
    {
        if ((sender as Control)?.Tag is not string slipId)
        {
            return;
        }

        var node = FindTreeNode(projectTree.ItemsSource as IEnumerable<KastnTreeNode>, slipId);
        if (node is not null && !ReferenceEquals(projectTree.SelectedItem, node))
        {
            projectTree.SelectedItem = node;
        }
    }

    // Highlight the tree-selected slip's block and scroll it into view (the tree → View
    // half of the bridge). A pure selection change reaches here without a rebuild.
    private void UpdateViewSelectionHighlight(bool scrollIntoView = true)
    {
        if (boardModeActive)
        {
            UpdateBoardSelectionHighlight(scrollIntoView);
            return;
        }

        if (highlightedViewSlipId is not null
            && viewSlipBlocks.TryGetValue(highlightedViewSlipId, out var previous))
        {
            ApplyBlockHighlight(previous, on: false);
        }

        highlightedViewSlipId = null;
        var selectedId = SelectedTreeNode?.Slip?.Id ?? editorState.SlipId;
        if (selectedId is null || !viewSlipBlocks.TryGetValue(selectedId, out var block))
        {
            return;
        }

        ApplyBlockHighlight(block, on: true);
        highlightedViewSlipId = selectedId;
        if (scrollIntoView)
        {
            block.BringIntoView();
            // A freshly rebuilt block may not be laid out yet; retry after layout.
            Dispatcher.UIThread.Post(block.BringIntoView, DispatcherPriority.Background);
        }
    }

    // Restore the document scroll after a rebuild. Runs after layout (so the new
    // extent is known) and before paint, so the rebuild's momentary reset to the
    // top is never visible.
    private void RestoreViewScroll(Vector savedOffset)
    {
        // Force a synchronous layout so the rebuilt content's extent is known, then
        // restore the offset immediately — before the frame paints — so the rebuild
        // never visibly flashes to the top. (Posting it a frame later showed the flash.)
        viewerDocumentScroll.UpdateLayout();
        var maxY = Math.Max(
            0,
            viewerDocumentScroll.Extent.Height - viewerDocumentScroll.Viewport.Height);
        viewerDocumentScroll.Offset = new Vector(
            savedOffset.X,
            Math.Min(savedOffset.Y, maxY));
    }

    private void ApplyBlockHighlight(Border block, bool on)
    {
        block.BorderBrush = on ? ThemeBrush("ZetlAccentBrush") ?? Brushes.Transparent : Brushes.Transparent;
        block.Background = on ? ThemeBrush("ZetlSurfaceBrush") ?? Brushes.Transparent : Brushes.Transparent;
    }

    private IBrush? ThemeBrush(string key) =>
        this.TryFindResource(key, out var value) && value is IBrush brush ? brush : null;

    private static TextAlignment SlipTextAlignment(ZetlSlipSnapshot slip) =>
        ZetlViewRenderer.SlipAlignment(slip) switch
        {
            "center" => TextAlignment.Center,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Left
        };

    // Render a slip's Markdown blocks into the slip block: paragraphs as wrapped text
    // (honoring alignment) and lists as marker + content rows with a hanging indent.
    private void AppendSlipBlocks(StackPanel content, ZetlSlipSnapshot slip, string text)
    {
        var alignment = SlipTextAlignment(slip);
        // A whole-note kind (heading/quote/code/divider) synthesizes its one block; any
        // other kind parses the body normally.
        foreach (var block in ZetlMarkdown.BlocksForNote(
            slip.BlockKind, text, ZetlViewRenderer.EffectiveInlineStyles(slip, text)))
        {
            if (block is ZetlParagraphBlock paragraph)
            {
                var textBlock = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = alignment
                };
                for (var line = 0; line < paragraph.Lines.Count; line++)
                {
                    if (line > 0)
                    {
                        textBlock.Inlines!.Add(new LineBreak());
                    }

                    AppendInlines(textBlock.Inlines!, paragraph.Lines[line]);
                }

                content.Children.Add(textBlock);
            }
            else if (block is ZetlListBlock list)
            {
                var number = list.Start;
                foreach (var item in list.Items)
                {
                    var marker = list.Kind switch
                    {
                        "ordered" => $"{number++}.",
                        "task" => item.Checked ? "☑" : "☐",
                        _ => "•"
                    };
                    var row = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                        Margin = new Avalonia.Thickness(8, 1, 0, 1)
                    };
                    var markerBlock = new TextBlock
                    {
                        Text = marker,
                        MinWidth = 16,
                        Margin = new Avalonia.Thickness(0, 0, 6, 0)
                    };
                    Grid.SetColumn(markerBlock, 0);
                    var itemBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };
                    AppendInlines(itemBlock.Inlines!, item.Inlines);
                    Grid.SetColumn(itemBlock, 1);
                    row.Children.Add(markerBlock);
                    row.Children.Add(itemBlock);
                    content.Children.Add(row);
                }
            }
            else if (block is ZetlHeadingBlock heading)
            {
                // Softened sub-heading: bold and slightly larger, never a section heading.
                var headingBlock = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = alignment,
                    FontWeight = FontWeight.Bold,
                    FontSize = heading.Level <= 1 ? 17 : heading.Level == 2 ? 15.5 : 14,
                    Margin = new Avalonia.Thickness(0, 6, 0, 1)
                };
                AppendInlines(headingBlock.Inlines!, heading.Inlines);
                content.Children.Add(headingBlock);
            }
            else if (block is ZetlQuoteBlock quote)
            {
                var quoteText = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = alignment,
                    FontStyle = FontStyle.Italic
                };
                for (var line = 0; line < quote.Lines.Count; line++)
                {
                    if (line > 0)
                    {
                        quoteText.Inlines!.Add(new LineBreak());
                    }

                    AppendInlines(quoteText.Inlines!, quote.Lines[line]);
                }

                content.Children.Add(new Border
                {
                    BorderThickness = new Avalonia.Thickness(3, 0, 0, 0),
                    BorderBrush = ThemeBrush("ZetlBorderBrush") ?? Brushes.Gray,
                    Padding = new Avalonia.Thickness(8, 2, 0, 2),
                    Margin = new Avalonia.Thickness(2, 2, 0, 2),
                    Child = quoteText
                });
            }
            else if (block is ZetlCodeBlock code)
            {
                var codeText = new TextBlock
                {
                    Text = code.Text,
                    TextWrapping = TextWrapping.Wrap
                };
                if (ThemeFont("ZetlMonoFontFamily") is { } mono)
                {
                    codeText.FontFamily = mono;
                }

                content.Children.Add(new Border
                {
                    Classes = { "surface" },
                    Padding = new Avalonia.Thickness(8, 6),
                    Margin = new Avalonia.Thickness(0, 2, 0, 2),
                    Child = codeText
                });
            }
            else if (block is ZetlDividerBlock)
            {
                content.Children.Add(new Border
                {
                    Height = 1,
                    Background = ThemeBrush("ZetlBorderBrush") ?? Brushes.Gray,
                    Margin = new Avalonia.Thickness(0, 6, 0, 6)
                });
            }
        }
    }

    // Walk the Markdown inline AST into Avalonia inlines. Links render as accent
    // underlined text (visual only on-screen; exported HTML/PDF carry the href).
    private void AppendInlines(InlineCollection target, IReadOnlyList<ZetlInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case ZetlTextRun run:
                    target.Add(new Run(run.Text));
                    break;
                case ZetlCodeRun code:
                    var codeRun = new Run(code.Text);
                    if (ThemeFont("ZetlMonoFontFamily") is { } mono)
                    {
                        codeRun.FontFamily = mono;
                    }
                    target.Add(codeRun);
                    break;
                case ZetlEmphasis emphasis:
                    var span = new Span();
                    AppendInlines(span.Inlines, emphasis.Children);
                    switch (emphasis.Kind)
                    {
                        case "bold":
                            span.FontWeight = FontWeight.Bold;
                            break;
                        case "italic":
                            span.FontStyle = FontStyle.Italic;
                            break;
                        case "strike":
                            span.TextDecorations = TextDecorations.Strikethrough;
                            break;
                    }
                    target.Add(span);
                    break;
                case ZetlLink link:
                    var linkSpan = new Span { TextDecorations = TextDecorations.Underline };
                    if (ThemeBrush("ZetlAccentBrush") is { } accent)
                    {
                        linkSpan.Foreground = accent;
                    }
                    AppendInlines(linkSpan.Inlines, link.Children);
                    target.Add(linkSpan);
                    break;
                case ZetlWikiLink wiki:
                    var resolved = currentProject?.Slips.Any(s => s.Id == wiki.TargetId) ?? false;
                    if (resolved)
                    {
                        var linkBlock = new TextBlock
                        {
                            Text = wiki.CachedTitle,
                            TextDecorations = TextDecorations.Underline,
                            Cursor = new Cursor(StandardCursorType.Hand),
                            Foreground = ThemeBrush("ZetlAccentBrush") ?? Brushes.Blue
                        };
                        var targetId = wiki.TargetId;
                        linkBlock.PointerPressed += (s, e) =>
                        {
                            e.Handled = true;
                            var node = FindTreeNode(projectTree.ItemsSource as IEnumerable<KastnTreeNode>, targetId);
                            if (node is not null && !ReferenceEquals(projectTree.SelectedItem, node))
                            {
                                projectTree.SelectedItem = node;
                            }
                        };
                        target.Add(new InlineUIContainer(linkBlock));
                    }
                    else
                    {
                        var unresolvedSpan = new Span { Foreground = Brushes.Gray };
                        unresolvedSpan.Inlines.Add(new Run(wiki.CachedTitle));
                        target.Add(unresolvedSpan);
                    }
                    break;
            }
        }
    }

    private FontFamily? ThemeFont(string key) =>
        this.TryFindResource(key, out var value) && value is FontFamily font ? font : null;

    private async Task LoadPicturePreviewAsync(
        ZetlSlipSnapshot slip,
        Avalonia.Controls.Image image,
        TextBlock status,
        int generation)
    {
        try
        {
            var content = await GetPictureContentAsync(slip);
            if (generation != pictureRenderGeneration || content is null)
            {
                if (generation == pictureRenderGeneration)
                {
                    status.Text = "Picture unavailable.";
                }
                return;
            }

            // The cache owns the bitmap, so a stale load neither disposes nor
            // assigns — the next rebuild picks the decoded bitmap up synchronously.
            var bitmap = DecodeAndCachePicture(content, slip.Picture!.Sha256, 1100);
            if (generation != pictureRenderGeneration)
            {
                return;
            }

            image.Source = bitmap;
            status.IsVisible = false;
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (generation == pictureRenderGeneration)
            {
                status.Text = "Picture unavailable.";
            }
        }
    }

    private async Task<ZetlPictureContent?> GetPictureContentAsync(ZetlSlipSnapshot slip)
    {
        if (currentProject is null || slip.Picture is null)
        {
            return null;
        }

        var cacheKey = slip.Picture.Sha256;
        if (pictureCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        if (!pictureLoads.TryGetValue(cacheKey, out var loading))
        {
            loading = FetchPictureContentAsync(currentProject.Id, slip, cacheKey);
            pictureLoads[cacheKey] = loading;
        }

        try
        {
            return await loading;
        }
        finally
        {
            pictureLoads.Remove(cacheKey);
        }
    }

    private async Task<ZetlPictureContent?> FetchPictureContentAsync(
        string projectId,
        ZetlSlipSnapshot slip,
        string cacheKey)
    {
        var response = await connection.QueryAsync(new ZetlCommandEnvelope
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Kind = ZetlCommandKind.GetSlipPicture,
            ProjectId = projectId,
            TargetId = slip.Id
        });
        var content = response.Status == ZetlResponseStatus.Success
            ? response.Payload?.Deserialize<ZetlPictureContent>(ZetlProtocolJson.Options)
            : null;
        if (content is null
            || content.Bytes.Length == 0
            || !string.Equals(content.Sha256, cacheKey, StringComparison.Ordinal))
        {
            return null;
        }

        CachePicture(cacheKey, content);
        return content;
    }

    private void CachePicture(string cacheKey, ZetlPictureContent content)
    {
        if (content.Bytes.LongLength > MaximumPictureCacheBytes)
        {
            return;
        }

        while (pictureCacheBytes + content.Bytes.LongLength > MaximumPictureCacheBytes
            && pictureCacheOrder.TryDequeue(out var expired))
        {
            if (pictureCache.Remove(expired, out var removed))
            {
                pictureCacheBytes -= removed.Bytes.LongLength;
            }
        }

        if (pictureCache.TryAdd(cacheKey, content))
        {
            pictureCacheOrder.Enqueue(cacheKey);
            pictureCacheBytes += content.Bytes.LongLength;
        }
    }

    private async Task<IReadOnlyDictionary<string, ZetlPictureContent>> LoadPictureContentsAsync(
        IReadOnlyList<ZetlSlipSnapshot> slips)
    {
        var result = new Dictionary<string, ZetlPictureContent>(StringComparer.Ordinal);
        foreach (var slip in slips.Where(slip => slip.Type == ZetlSlipType.Picture))
        {
            try
            {
                if (await GetPictureContentAsync(slip) is { } content)
                {
                    result[slip.Id] = content;
                }
            }
            catch (Exception ex) when (
                ex is IOException or InvalidOperationException or OperationCanceledException)
            {
                // Keep rendering the rest; unavailable pictures get a readable placeholder.
            }
        }

        return result;
    }

    private void ClearViewDocument()
    {
        pictureRenderGeneration++;
        viewerDocumentPanel.Children.Clear();
        viewSlipBlocks.Clear();
        viewSlipBlockCache.Clear();
        viewHeadingCache.Clear();
        viewGroupBoxCache.Clear();
        highlightedViewSlipId = null;
    }

    // A cached decoded bitmap for the slip's picture at the given decode width,
    // or null when it has not been decoded yet. A hit lets the caller assign the
    // image synchronously, so a rebuild neither re-decodes nor blinks "Loading…".
    private Bitmap? CachedDecodedPicture(ZetlSlipSnapshot slip, int width) =>
        slip.Picture is { } picture
        && decodedPictureCache.TryGetValue((picture.Sha256, width), out var bitmap)
            ? bitmap
            : null;

    private Bitmap DecodeAndCachePicture(ZetlPictureContent content, string sha, int width)
    {
        if (decodedPictureCache.TryGetValue((sha, width), out var existing))
        {
            return existing;
        }

        using var stream = new MemoryStream(content.Bytes, writable: false);
        var bitmap = Bitmap.DecodeToWidth(stream, width);
        decodedPictureCache[(sha, width)] = bitmap;
        return bitmap;
    }

    private void ClearDecodedPictureCache()
    {
        foreach (var bitmap in decodedPictureCache.Values)
        {
            bitmap.Dispose();
        }

        decodedPictureCache.Clear();
    }

    private ZetlViewDocument SelectedView =>
        viewPickerBox.SelectedItem as ZetlViewDocument
        ?? (loadedViews.Count > 0 ? loadedViews[0] : ZetlViewDefaults.CreateAll()[0]);

    private void SelectViewForProject(ZetlProjectSnapshot project)
    {
        var target = string.IsNullOrEmpty(project.DefaultViewId)
            ? null
            : loadedViews.FirstOrDefault(view => view.Id == project.DefaultViewId);
        if (target is null)
        {
            // No project-pinned view: fall back to the user's global default reading
            // view from Zetl Settings, then to the first available view.
            var globalId = CurrentAppSettings().KastnDefaultViewId;
            if (!string.IsNullOrEmpty(globalId))
            {
                target = loadedViews.FirstOrDefault(view => view.Id == globalId);
            }
        }

        viewPickerBox.SelectedItem = target ?? loadedViews.FirstOrDefault();
    }

    // The Kastn workbench preferences live in the shared Zetl settings file, edited
    // from Zetl's one Settings window. Read fresh at each use so a change made in Zetl
    // takes effect without restarting Kastn; these read points are all infrequent.
    private static ZetlAppSettings? cachedAppSettings;
    private static DateTime cachedAppSettingsAt;
    private static int cachedAppSettingsStamp;

    // Settings live in a JSON file rewritten by Zetl; a fresh read per call put
    // disk IO inside per-slip render loops. A short-lived cache keeps hot paths
    // off the disk: in-process saves invalidate it immediately (the save stamp),
    // and external settings changes from Zetl still land within a second.
    private static ZetlAppSettings CurrentAppSettings()
    {
        var now = DateTime.UtcNow;
        if (cachedAppSettings is null
            || cachedAppSettingsStamp != ZetlAppSettingsStore.SaveStamp
            || now - cachedAppSettingsAt > TimeSpan.FromSeconds(1))
        {
            cachedAppSettings = new ZetlAppSettingsStore().Settings;
            cachedAppSettingsAt = now;
            cachedAppSettingsStamp = ZetlAppSettingsStore.SaveStamp;
        }

        return cachedAppSettings;
    }

    private void RefreshViewCatalog(ZetlProjectSnapshot? project, string? selectId = null)
    {
        globalViews = viewStore.LoadAll();
        var projectViews = project?.Views
            .Select(ZetlProjectSnapshotMapper.ToDocument)
            .ToList() ?? [];
        loadedViews = globalViews
            .Where(view => projectViews.All(projectView => projectView.Id != view.Id))
            .Concat(projectViews)
            .ToList();
        viewPickerBox.ItemsSource = loadedViews;
        viewPickerBox.SelectedItem = loadedViews.FirstOrDefault(view => view.Id == selectId)
            ?? loadedViews.FirstOrDefault();
    }

    private bool IsProjectScopedView(string viewId) =>
        currentProject?.Views.Any(view => view.Id == viewId) == true;

    private async Task CopyRenderedViewAsync()
    {
        if (currentProject is null
            || lastRenderedViewText.Length == 0
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        var visible = CurrentViewSlips();
        var pictures = await LoadPictureContentsAsync(visible);
        var rendered = ZetlViewRenderer.Render(
            currentProject,
            visible,
            SelectedView,
            pictures,
            CurrentAppSettings().KastnPreferSlipKindOverBucketKind);
        await clipboard.SetTextAsync(rendered);
        statusText.Text = $"Copied the {SelectedView.Name} view to the clipboard.";
    }

    private async Task ExportRenderedViewAsync()
    {
        if (currentProject is null)
        {
            return;
        }

        var view = SelectedView;
        var isPdf = view.Kind == ZetlViewKinds.Pdf;
        if (!isPdf && lastRenderedViewText.Length == 0)
        {
            return;
        }

        var extension = ViewFileExtension(view.Kind);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export {view.Name}",
            SuggestedFileName = $"{SafeFileName(currentProject.Name)}.{extension}",
            DefaultExtension = extension
        });
        if (file is null)
        {
            return;
        }

        try
        {
            var visible = CurrentViewSlips();
            var pictures = await LoadPictureContentsAsync(visible);
            await using var stream = await file.OpenWriteAsync();
            if (isPdf)
            {
                var pdf = KastnPdfRenderer.Render(
                    currentProject,
                    visible,
                    view,
                    pictures,
                    CurrentAppSettings().KastnPreferSlipKindOverBucketKind);
                await stream.WriteAsync(pdf);
            }
            else
            {
                var rendered = ZetlViewRenderer.Render(
                    currentProject,
                    visible,
                    view,
                    pictures,
                    CurrentAppSettings().KastnPreferSlipKindOverBucketKind);
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(rendered);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            statusText.Text = $"Could not export the view: {ex.Message}";
            return;
        }

        statusText.Text = $"Exported the {view.Name} view to {file.Name}.";
    }

    private static string ViewFileExtension(string kind) => kind switch
    {
        ZetlViewKinds.Markdown => "md",
        ZetlViewKinds.Html => "html",
        ZetlViewKinds.Pdf => "pdf",
        ZetlViewKinds.Tsv => "tsv",
        _ => "txt"
    };

    private static string SafeFileName(string name)
    {
        var cleaned = string.Concat(name.Trim().Select(character =>
            Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0 ? '-' : character));
        return string.IsNullOrWhiteSpace(cleaned) ? "project" : cleaned;
    }

    // ---- In-window view editor (mirrors the template editor) ----

    private void EditSelectedView()
    {
        var view = SelectedView;
        if (ZetlViewDefaults.IsBuiltIn(view.Id))
        {
            // Built-ins stay immutable: edit a fresh user copy instead.
            OpenViewEditor(ZetlViewDefaults.Duplicate(view), isNew: true);
        }
        else
        {
            OpenViewEditor(ZetlViewDefaults.Clone(view), isNew: false);
        }
    }

    private void OpenViewEditor(ZetlViewDocument working, bool isNew)
    {
        editingView = working;
        editingProjectScopedView = !isNew && IsProjectScopedView(working.Id);
        viewErrorText.IsVisible = false;

        viewEditorUpdating = true;
        viewEditorTitle.Text = isNew ? "New View" : $"Edit View — {working.Name}";
        viewNameBox.Text = working.Name;
        viewDescriptionBox.Text = working.Description;
        viewKindBox.SelectedItem = ViewKindChoices.Contains(working.Kind)
            ? working.Kind
            : ZetlViewKinds.Formatted;
        viewTsvRowBox.Value = Math.Clamp(working.TsvRowLength, 1, 100);
        viewListStyleBox.SelectedItem = ZetlViewListStyles.Normalize(working.ListStyle);
        viewNumberHeadingsCheck.IsChecked = working.NumberHeadings;
        viewShowTitleCheck.IsChecked = working.ShowTitle;
        viewTitleBox.Text = working.Title;
        LoadViewStructureEditor(working.Sections);
        viewEditorUpdating = false;

        ApplyViewKindSettingsVisibility();
        viewBaselineJson = CurrentViewJson();

        emptyState.IsVisible = false;
        projectView.IsVisible = false;
        templateEditorView.IsVisible = false;
        viewEditorView.IsVisible = true;
        RefreshViewLivePreview();
    }

    private void SetViewEditorKind(string kind)
    {
        viewKindBox.SelectedItem = kind;
        ApplyViewKindSettingsVisibility();
        RefreshViewLivePreview();
    }

    private void ApplyViewKindSettingsVisibility()
    {
        var kind = viewKindBox.SelectedItem as string ?? ZetlViewKinds.Formatted;
        viewTsvPanel.IsVisible = kind == ZetlViewKinds.Tsv;
        viewDocumentSettingsPanel.IsVisible = kind is ZetlViewKinds.Markdown
            or ZetlViewKinds.Html
            or ZetlViewKinds.Pdf;

        SetViewKindButtonState(viewFormattedKindButton, kind == ZetlViewKinds.Formatted);
        SetViewKindButtonState(viewPlainKindButton, kind == ZetlViewKinds.Plain);
        SetViewKindButtonState(viewTsvKindButton, kind == ZetlViewKinds.Tsv);
        SetViewKindButtonState(viewMarkdownKindButton, kind == ZetlViewKinds.Markdown);
        SetViewKindButtonState(viewHtmlKindButton, kind == ZetlViewKinds.Html);
        SetViewKindButtonState(viewPdfKindButton, kind == ZetlViewKinds.Pdf);
    }

    private static void SetViewKindButtonState(Button button, bool isActive)
    {
        button.Classes.Set("view-format-active", isActive);
    }

    private string CurrentViewJson()
    {
        var doc = CurrentViewDocument();
        return doc is null ? "" : JsonSerializer.Serialize(doc, JsonFile.Options);
    }

    private ZetlViewDocument? CurrentViewDocument()
    {
        if (editingView is null)
        {
            return null;
        }

        var doc = ZetlViewDefaults.Clone(editingView);
        doc.Name = viewNameBox.Text?.Trim() ?? "";
        doc.Description = viewDescriptionBox.Text?.Trim() ?? "";
        doc.Kind = viewKindBox.SelectedItem as string ?? ZetlViewKinds.Formatted;
        doc.TsvRowLength = (int)(viewTsvRowBox.Value ?? 5);
        doc.ListStyle = viewListStyleBox.SelectedItem as string ?? ZetlViewListStyles.Bullet;
        doc.NumberHeadings = viewNumberHeadingsCheck.IsChecked == true;
        doc.ShowTitle = viewShowTitleCheck.IsChecked == true;
        doc.Title = viewTitleBox.Text?.Trim() ?? "";
        doc.Sections = CurrentViewSections();
        return doc;
    }

    private bool IsViewDirty() =>
        editingView is not null && CurrentViewJson() != viewBaselineJson;

    private async Task CancelViewEditAsync()
    {
        if (IsViewDirty())
        {
            var discard = await KastnDialogs.ConfirmAsync(
                this,
                "Discard unsaved changes to this view?",
                "Discard");
            if (!discard)
            {
                return;
            }
        }

        CloseViewEditor();
    }

    private async Task SaveViewAsync()
    {
        if (editingView is not { } view)
        {
            return;
        }

        view.Name = viewNameBox.Text?.Trim() ?? "";
        view.Description = viewDescriptionBox.Text?.Trim() ?? "";
        view.Kind = viewKindBox.SelectedItem as string ?? ZetlViewKinds.Formatted;
        view.TsvRowLength = (int)(viewTsvRowBox.Value ?? 5);
        view.ListStyle = viewListStyleBox.SelectedItem as string ?? ZetlViewListStyles.Bullet;
        view.NumberHeadings = viewNumberHeadingsCheck.IsChecked == true;
        view.ShowTitle = viewShowTitleCheck.IsChecked == true;
        view.Title = viewTitleBox.Text?.Trim() ?? "";
        view.Sections = CurrentViewSections();
        if (string.IsNullOrEmpty(view.Id))
        {
            view.Id = ZetlViewDefaults.CreateId(view.Name);
        }

        var errors = ZetlViewValidator.Validate(view);
        if (errors.Count > 0)
        {
            viewErrorText.Text = string.Join("\n", errors);
            viewErrorText.IsVisible = true;
            return;
        }

        var saveProjectScoped = view.Sections.Count > 0;
        if (saveProjectScoped)
        {
            if (currentProject is null)
            {
                viewErrorText.Text = "Open a project before saving a structured view.";
                viewErrorText.IsVisible = true;
                return;
            }

            var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.SaveProjectView,
                new SaveProjectViewCommand { View = ZetlProjectSnapshotMapper.ToSnapshot(view) },
                currentProject.Id,
                expectedTargetRevision: currentProject.MetadataRevision));
            if (response.Status != ZetlResponseStatus.Success)
            {
                viewErrorText.Text = response.Error?.Message ?? $"Save failed: {response.Status}.";
                viewErrorText.IsVisible = true;
                return;
            }

            // A formerly global structured view becomes project-owned after the
            // authoritative project write succeeds.
            if (!editingProjectScopedView && !ZetlViewDefaults.IsBuiltIn(view.Id))
            {
                try
                {
                    viewStore.Delete(view.Id);
                }
                catch (IOException)
                {
                    // The project copy is durable; a stale global copy is hidden by id.
                }
            }
        }
        else
        {
            try
            {
                viewStore.Save(view);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
            {
                viewErrorText.Text = ex.Message;
                viewErrorText.IsVisible = true;
                return;
            }

            if (editingProjectScopedView && currentProject is not null)
            {
                var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                    Guid.NewGuid().ToString("N"),
                    ZetlCommandKind.DeleteProjectView,
                    new DeleteProjectViewCommand { ViewId = view.Id },
                    currentProject.Id,
                    expectedTargetRevision: currentProject.MetadataRevision));
                if (response.Status != ZetlResponseStatus.Success)
                {
                    viewErrorText.Text = response.Error?.Message ?? $"Scope change failed: {response.Status}.";
                    viewErrorText.IsVisible = true;
                    return;
                }
            }
        }

        var savedId = view.Id;
        var savedName = view.Name;
        CloseViewEditor();
        await connection.RefreshAsync();
        currentProject = connection.Current.Project;
        RefreshViewCatalog(currentProject, savedId);
        RefreshViewer();
        statusText.Text = $"Saved view '{savedName}'.";
    }

    private void CloseViewEditor()
    {
        editingView = null;
        editingProjectScopedView = false;
        viewErrorText.IsVisible = false;
        viewEditorView.IsVisible = false;
        if (currentProject is not null)
        {
            projectView.IsVisible = true;
            emptyState.IsVisible = false;
        }
        else
        {
            emptyState.IsVisible = true;
        }
    }

    private async Task DeleteSelectedViewAsync()
    {
        var view = SelectedView;
        if (ZetlViewDefaults.IsBuiltIn(view.Id))
        {
            statusText.Text = "Built-in views can't be deleted.";
            return;
        }

        var confirmed = await KastnDialogs.ConfirmAsync(
            this,
            $"Delete the view '{view.Name}'? This cannot be undone.",
            "Delete");
        if (!confirmed)
        {
            return;
        }

        if (IsProjectScopedView(view.Id) && currentProject is not null)
        {
            var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.DeleteProjectView,
                new DeleteProjectViewCommand { ViewId = view.Id },
                currentProject.Id,
                expectedTargetRevision: currentProject.MetadataRevision));
            if (response.Status != ZetlResponseStatus.Success)
            {
                statusText.Text = response.Error?.Message ?? $"Delete failed: {response.Status}.";
                return;
            }
        }
        else
        {
            try
            {
                viewStore.Delete(view.Id);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                statusText.Text = $"Could not delete view: {ex.Message}";
                return;
            }
        }

        await connection.RefreshAsync();
        currentProject = connection.Current.Project;
        RefreshViewCatalog(currentProject);
        RefreshViewer();
        statusText.Text = $"Deleted view '{view.Name}'.";
    }

    // Re-read the view catalog from disk, restoring the selection (by id when given,
    // otherwise the first view) and re-rendering the read view.
    private void ReloadViews(string? selectId)
    {
        RefreshViewCatalog(currentProject, selectId);
        RefreshViewer();
    }

    private List<KastnTreeNode> FlattenBucketNodes(IEnumerable<KastnTreeNode> nodes)
    {
        var list = new List<KastnTreeNode>();
        foreach (var node in nodes)
        {
            if (node.Kind == KastnTreeNodeKind.Bucket)
            {
                list.Add(node);
                list.AddRange(FlattenBucketNodes(node.Children));
            }
        }
        return list;
    }

    // Each board column's cards panel by bucket id, so the drag hit-test can pick
    // the precise insertion slot from the pointer's place among the cards.
    private readonly Dictionary<string, StackPanel> boardColumnCardPanels = new(StringComparer.Ordinal);

    // Reconciled board state, keyed by bucket/slip id, so a refresh updates only
    // what changed instead of rebuilding the whole board (which flashed and reset
    // every scroll position). A card is reused while its render inputs are
    // unchanged; its picture bitmap is card-owned and disposed with it.
    private sealed class BoardColumnUi
    {
        public required Grid Wrapper { get; init; }
        public required TextBlock TitleText { get; init; }
        public required TextBlock CountText { get; init; }
        public required StackPanel CardsPanel { get; init; }

        // The inline "type a card in place" composer under the cards; it lives
        // outside the reconciled cards panel so refreshes never disturb it.
        public required Border Composer { get; init; }
        public required TextBox ComposerBox { get; init; }
        public bool ComposerBusy { get; set; }
    }

    private sealed class BoardCardUi
    {
        public required Grid Wrapper { get; init; }
        public required Border CardBorder { get; init; }
        public required string RenderKey { get; init; }

        // The thumbnail targets of a picture card, waiting for the async load
        // (the decoded bitmap lives in the shared decodedPictureCache). The
        // reconcile starts the load after the card is registered, so a load that
        // completes synchronously still sees itself as the current card.
        public (Image Image, TextBlock Status)? PendingPictureLoad { get; set; }
    }

    private readonly Dictionary<string, BoardColumnUi> boardColumns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BoardCardUi> boardCards = new(StringComparer.Ordinal);

    private void BuildBoardView(IReadOnlyList<ZetlSlipSnapshot> visible)
    {
        if (currentProject is null)
        {
            ClearBoard();
            return;
        }

        // The active tree nodes (which exclude the Deleted bucket), flattened to
        // pre-order traversal so columns follow the canonical bucket order.
        var treeNodes = KastnWorkbench.BuildProjectTree(currentProject, currentProject.Slips, deletedOnly: false);
        var buckets = FlattenBucketNodes(treeNodes);
        var visibleSlipsByBucket = visible
            .Where(slip => !IsSlipInDeleted(slip))
            .GroupBy(slip => slip.BucketId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ZetlSlipSnapshot>)g.ToList(), StringComparer.Ordinal);

        var desiredColumns = new List<Control>();
        var liveBucketIds = new HashSet<string>(StringComparer.Ordinal);
        var liveSlipIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bucketNode in buckets)
        {
            var bucket = bucketNode.Bucket;
            if (bucket is null)
            {
                continue;
            }

            liveBucketIds.Add(bucket.Id);
            var slips = visibleSlipsByBucket.TryGetValue(bucket.Id, out var found) ? found : [];
            if (!boardColumns.TryGetValue(bucket.Id, out var column))
            {
                column = CreateBoardColumn(bucket);
                boardColumns[bucket.Id] = column;
            }

            // Per-refresh state: the header text and the node context. The tree
            // rebuilds its nodes on every snapshot, and the drag markers bind the
            // node's drop flags, so a reused column must point at the fresh node.
            column.TitleText.Text = bucket.Name;
            column.CountText.Text = $"({slips.Count})";
            column.Wrapper.DataContext =
                FindTreeNode(projectTree.ItemsSource as IEnumerable<KastnTreeNode>, bucket.Id);

            ReconcileColumnCards(column, bucket, slips, liveSlipIds);
            desiredColumns.Add(column.Wrapper);
        }

        foreach (var staleId in boardColumns.Keys.Where(id => !liveBucketIds.Contains(id)).ToList())
        {
            boardColumns.Remove(staleId);
            boardColumnCardPanels.Remove(staleId);
        }

        foreach (var staleId in boardCards.Keys.Where(id => !liveSlipIds.Contains(id)).ToList())
        {
            RemoveBoardCard(staleId);
        }

        SyncPanelChildren(boardColumnsPanel.Children, desiredColumns);
    }

    private void ClearBoard()
    {
        foreach (var id in boardCards.Keys.ToList())
        {
            RemoveBoardCard(id);
        }

        boardColumns.Clear();
        boardColumnCardPanels.Clear();
        boardColumnsPanel.Children.Clear();
        boardSlipCards.Clear();
        highlightedBoardSlipId = null;
    }

    private void ReconcileColumnCards(
        BoardColumnUi column,
        ZetlBucketSnapshot bucket,
        IReadOnlyList<ZetlSlipSnapshot> slips,
        HashSet<string> liveSlipIds)
    {
        var desiredCards = new List<Control>();
        foreach (var slip in slips)
        {
            liveSlipIds.Add(slip.Id);
            var renderKey = BoardCardRenderKey(slip, bucket);
            if (boardCards.TryGetValue(slip.Id, out var card)
                && !string.Equals(card.RenderKey, renderKey, StringComparison.Ordinal))
            {
                // Content changed: rebuild this one card.
                RemoveBoardCard(slip.Id);
                card = null;
            }

            if (card is null)
            {
                card = CreateBoardCard(slip, renderKey);
                boardCards[slip.Id] = card;
                boardSlipCards[slip.Id] = card.CardBorder;
                if (card.PendingPictureLoad is { } load)
                {
                    card.PendingPictureLoad = null;
                    LoadBoardCardPictureAsync(slip, card, load.Image, load.Status);
                }
            }

            card.Wrapper.DataContext =
                FindTreeNode(projectTree.ItemsSource as IEnumerable<KastnTreeNode>, slip.Id);
            desiredCards.Add(card.Wrapper);
        }

        SyncPanelChildren(column.CardsPanel.Children, desiredCards);
    }

    // The inputs a rendered card depends on beyond its position: the slip's own
    // revision plus the bucket/setting context that shapes its footer markers.
    private string BoardCardRenderKey(ZetlSlipSnapshot slip, ZetlBucketSnapshot bucket) =>
        $"{slip.Revision}|{bucket.RenderKind}|{CurrentAppSettings().KastnPreferSlipKindOverBucketKind}|{UntitledSlipTitle}";

    private void RemoveBoardCard(string slipId)
    {
        if (!boardCards.TryGetValue(slipId, out var card))
        {
            return;
        }

        boardCards.Remove(slipId);
        boardSlipCards.Remove(slipId);
        if (card.Wrapper.Parent is Panel parent)
        {
            parent.Children.Remove(card.Wrapper);
        }
    }

    // Make the panel's children match the desired sequence with minimal moves, so
    // untouched controls keep their layout and scroll state. A control arriving
    // from another panel (a card moved across columns) is detached first.
    private static void SyncPanelChildren(Avalonia.Controls.Controls children, IReadOnlyList<Control> desired)
    {
        var desiredSet = new HashSet<Control>(desired);
        for (var i = children.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(children[i]))
            {
                children.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var control = desired[i];
            if (i < children.Count && ReferenceEquals(children[i], control))
            {
                continue;
            }

            if (control.Parent is Panel elsewhere && !ReferenceEquals(elsewhere.Children, children))
            {
                elsewhere.Children.Remove(control);
            }

            var existing = children.IndexOf(control);
            if (existing >= 0)
            {
                children.Move(existing, i);
            }
            else
            {
                children.Insert(i, control);
            }
        }
    }

    // The reusable column shell: header, cards panel, drag wiring, and drop
    // overlays. The reconcile pass owns the changing parts — header text, node
    // DataContext, and the card list.
    private BoardColumnUi CreateBoardColumn(ZetlBucketSnapshot bucket)
    {
        var bucketId = bucket.Id;

        // Header elements
        var titleText = new TextBlock
        {
            Text = bucket.Name,
            FontWeight = FontWeight.Bold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var countText = new TextBlock
        {
            Text = "(0)",
            Classes = { "muted" },
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };

        var addCardButton = new Button
        {
            Content = "+",
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(addCardButton, "Add a card (Enter adds, Esc closes)");

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, 8),
            // Hit-testable everywhere so the whole header (not just its text) can
            // start a column drag; the + button keeps its click.
            Background = Brushes.Transparent
        };
        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(countText, 1);
        Grid.SetColumn(addCardButton, 2);
        headerGrid.Children.Add(titleText);
        headerGrid.Children.Add(countText);
        headerGrid.Children.Add(addCardButton);

        // Column-header drag reorders columns via ReorderBucket.
        headerGrid.PointerPressed += OnBoardColumnHeaderPointerPressed;
        headerGrid.PointerMoved += OnBoardColumnHeaderPointerMoved;
        headerGrid.PointerReleased += OnBoardColumnHeaderPointerReleased;

        // Cards list; the reconcile pass fills and maintains it.
        var cardsPanel = new StackPanel
        {
            Spacing = 6
        };

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = cardsPanel
        };

        // The inline composer: type a card in place instead of a modal round-trip.
        // Enter adds and keeps composing, Shift+Enter inserts a newline, Esc
        // discards, and leaving the box commits any typed text so it is never lost.
        var composerBox = new TextBox
        {
            Watermark = "New card",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };
        var composer = new Border
        {
            Background = ThemeBrush("ZetlSurfaceAltBrush"),
            BorderBrush = ThemeBrush("ZetlAccentBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 8, 0, 0),
            Child = composerBox,
            IsVisible = false
        };

        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };
        Grid.SetRow(headerGrid, 0);
        Grid.SetRow(scrollViewer, 1);
        Grid.SetRow(composer, 2);
        mainGrid.Children.Add(headerGrid);
        mainGrid.Children.Add(scrollViewer);
        mainGrid.Children.Add(composer);

        // The border inherits its KastnTreeNode DataContext from the wrapper, which
        // the reconcile pass repoints at the fresh tree node on every refresh.
        var columnBorder = new Border
        {
            Tag = "dragRow",
            Background = ThemeBrush("ZetlSurfaceBrush"),
            BorderBrush = ThemeBrush("ZetlBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Width = 280,
            Padding = new Thickness(8),
            Child = mainGrid,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Hook up drop events on the column border so users can drop cards onto empty column space
        DragDrop.SetAllowDrop(columnBorder, true);
        columnBorder.AddHandler(DragDrop.DragOverEvent, OnBoardDragOver);
        columnBorder.AddHandler(DragDrop.DragLeaveEvent, OnBoardDragLeave);
        columnBorder.AddHandler(DragDrop.DropEvent, OnBoardDrop);

        boardColumnCardPanels[bucketId] = cardsPanel;

        // Drag feedback overlays, bound to the same node flags the tree rows bind:
        // an accent outline while cards would drop into this column, and vertical
        // insertion lines at the left/right edges while a dragged column would land
        // before/after it.
        var columnWrapper = new Grid();
        columnWrapper.Children.Add(columnBorder);
        var intoOverlay = new Border
        {
            BorderBrush = ThemeBrush("ZetlAccentBrush"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            IsHitTestVisible = false,
            IsVisible = false
        };
        intoOverlay.Bind(Visual.IsVisibleProperty, new Avalonia.Data.Binding(nameof(KastnTreeNode.IsDropTarget)));
        columnWrapper.Children.Add(intoOverlay);
        columnWrapper.Children.Add(CreateBoardDropLine(horizontal: false, atStart: true, nameof(KastnTreeNode.ShowDropBefore)));
        columnWrapper.Children.Add(CreateBoardDropLine(horizontal: false, atStart: false, nameof(KastnTreeNode.ShowDropAfter)));

        var column = new BoardColumnUi
        {
            Wrapper = columnWrapper,
            TitleText = titleText,
            CountText = countText,
            CardsPanel = cardsPanel,
            Composer = composer,
            ComposerBox = composerBox
        };

        addCardButton.Click += (_, _) =>
        {
            composer.IsVisible = true;
            composerBox.Focus();
        };
        composerBox.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseBoardComposer(column);
            }
            else if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                await CommitBoardComposerAsync(bucketId, column, keepOpen: true);
            }
        }, RoutingStrategies.Tunnel);
        composerBox.LostFocus += async (_, _) =>
        {
            // Leaving the box commits typed text (capture must never silently drop
            // it) and closes; an empty composer just closes. A failed commit keeps
            // the composer open so the text stays recoverable.
            if (string.IsNullOrWhiteSpace(composerBox.Text))
            {
                CloseBoardComposer(column);
                return;
            }

            if (await CommitBoardComposerAsync(bucketId, column, keepOpen: false))
            {
                CloseBoardComposer(column);
            }
        };

        return column;
    }

    private static void CloseBoardComposer(BoardColumnUi column)
    {
        column.ComposerBox.Text = "";
        column.Composer.IsVisible = false;
    }

    // Send the composer text as a new slip at the end of the bucket. Returns true
    // when the card was added (or there was nothing to add); the composer clears
    // and, for Enter-to-add, stays focused for the next card.
    private async Task<bool> CommitBoardComposerAsync(string bucketId, BoardColumnUi column, bool keepOpen)
    {
        if (column.ComposerBusy)
        {
            return false;
        }

        var text = (column.ComposerBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            return true;
        }

        if (!IsOnline || currentProject is null)
        {
            statusText.Text = "Connect to Zetl to edit.";
            return false;
        }

        column.ComposerBusy = true;
        try
        {
            var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.AddSlip,
                new AddSlipCommand { BucketId = bucketId, Text = text, Source = "kastn" },
                currentProject.Id));
            if (response.Status != ZetlResponseStatus.Success)
            {
                statusText.Text = response.Error?.Message ?? $"Add failed: {response.Status}.";
                return false;
            }

            column.ComposerBox.Text = "";
            await connection.RefreshAsync();
            statusText.Text = "Card added.";
            if (keepOpen)
            {
                column.ComposerBox.Focus();
            }

            return true;
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            statusText.Text = ex.Message;
            return false;
        }
        finally
        {
            column.ComposerBusy = false;
        }
    }

    // An accent insertion line overlaying one edge of a board card or column,
    // visibility-bound to the node's drop-edge state: horizontal lines mark card
    // slots (top/bottom), vertical lines mark column slots (left/right).
    private Control CreateBoardDropLine(bool horizontal, bool atStart, string visibilityProperty)
    {
        var line = new Border
        {
            Background = ThemeBrush("ZetlAccentBrush"),
            IsHitTestVisible = false,
            IsVisible = false
        };
        if (horizontal)
        {
            line.Height = 2;
            line.VerticalAlignment = atStart ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        }
        else
        {
            line.Width = 3;
            line.HorizontalAlignment = atStart ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }

        line.Bind(Visual.IsVisibleProperty, new Avalonia.Data.Binding(visibilityProperty));
        return line;
    }

    // The reusable card: rebuilt only when its render key changes, so the closures
    // below always capture a snapshot equivalent to what is displayed. The node
    // DataContext lives on the wrapper and is repointed by the reconcile pass.
    private BoardCardUi CreateBoardCard(ZetlSlipSnapshot slip, string renderKey)
    {
        // Preview text
        var previewText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(slip.Text) ? UntitledSlipTitle : slip.Text,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 4)
        };

        var slipKind = ZetlViewRenderer.SlipBlockKind(slip);
        var bucket = currentProject?.Buckets.FirstOrDefault(b => b.Id == slip.BucketId);
        var preferSlipKindOverBucketKind = CurrentAppSettings().KastnPreferSlipKindOverBucketKind;
        var bucketListKind = !slip.IgnoreBucketRenderKind
            && !(preferSlipKindOverBucketKind && slipKind.Length > 0)
            && bucket?.RenderKind is ZetlBucketRenderKinds.Bullet
            or ZetlBucketRenderKinds.Ordered
            or ZetlBucketRenderKinds.Task
                ? bucket.RenderKind
                : "";
        var markerKind = bucketListKind.Length > 0
            ? bucketListKind
            : ZetlViewRenderer.IsListRenderKind(slipKind) ? slipKind : "";
        var innerKind = bucketListKind.Length > 0
            && ZetlViewRenderer.IsListRenderKind(slipKind)
            && !string.Equals(slipKind, bucketListKind, StringComparison.Ordinal)
                ? slipKind
                : "";

        // Footer layout (contains tags/markers/checkbox)
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 4, 0, 0)
        };

        void AddFooterMarker(string kind)
        {
            if (kind == ZetlBlockKinds.Task)
            {
                var taskCheck = new TextBlock
                {
                    Text = slip.Checked ? "☑" : "☐",
                    FontWeight = FontWeight.Bold,
                    Foreground = slip.Checked ? ThemeBrush("ZetlAccentBrush") : ThemeBrush("ZetlMutedTextBrush"),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Margin = new Thickness(0, 0, 4, 0)
                };
                taskCheck.PointerPressed += async (sender, args) =>
                {
                    args.Handled = true;
                    await ToggleSlipCheckedAsync(slip.Id);
                };
                footer.Children.Add(taskCheck);
            }
            else if (kind == ZetlBlockKinds.Bullet)
            {
                footer.Children.Add(new TextBlock { Text = "•", Classes = { "muted" } });
            }
            else if (kind == ZetlBlockKinds.Ordered)
            {
                footer.Children.Add(new TextBlock { Text = "1.", Classes = { "muted" } });
            }
        }

        AddFooterMarker(markerKind);
        AddFooterMarker(innerKind);

        // Capture origin icon/text if present
        if (slip.CaptureOrigin is { } origin)
        {
            var appName = !string.IsNullOrWhiteSpace(origin.ApplicationName) ? origin.ApplicationName : origin.ProcessName;
            if (!string.IsNullOrWhiteSpace(appName))
            {
                footer.Children.Add(new TextBlock
                {
                    Text = $"[{appName}]",
                    Classes = { "muted" },
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
        }

        var mainPanel = new StackPanel { Spacing = 4 };
        mainPanel.Children.Add(previewText);

        // Picture thumbnail; the async load starts once the card exists so the
        // bitmap can be card-owned (disposed when the card is rebuilt or removed).
        (Image Image, TextBlock Status)? pendingPictureLoad = null;
        if (slip.Type == ZetlSlipType.Picture && slip.Picture is not null)
        {
            var image = new Image
            {
                MaxWidth = 240,
                MaxHeight = 120,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var statusText = new TextBlock
            {
                Text = "Loading...",
                Classes = { "muted" },
                FontSize = 11
            };
            mainPanel.Children.Add(image);
            mainPanel.Children.Add(statusText);
            if (CachedDecodedPicture(slip, 260) is { } cachedThumbnail)
            {
                image.Source = cachedThumbnail;
                statusText.IsVisible = false;
            }
            else
            {
                pendingPictureLoad = (image, statusText);
            }
        }

        if (footer.Children.Count > 0)
        {
            mainPanel.Children.Add(footer);
        }

        // The border inherits its KastnTreeNode DataContext from the wrapper, which
        // the reconcile pass repoints at the fresh tree node on every refresh.
        var cardBorder = new Border
        {
            Tag = "dragRow",
            Background = ThemeBrush("ZetlSurfaceAltBrush"),
            BorderBrush = ThemeBrush("ZetlBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Child = mainPanel,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        // Selection click
        cardBorder.PointerPressed += (sender, args) =>
        {
            ReselectSlipNode(slip.Id);
        };

        // Setup dragging for the card
        cardBorder.PointerPressed += OnBoardCardPointerPressed;
        cardBorder.PointerMoved += OnBoardCardPointerMoved;
        cardBorder.PointerReleased += OnBoardCardPointerReleased;

        // Double click editor
        cardBorder.DoubleTapped += async (sender, args) =>
        {
            await EditBoardSlipAsync(slip);
        };

        // Setup drop target on the card (to reorder slips)
        DragDrop.SetAllowDrop(cardBorder, true);
        cardBorder.AddHandler(DragDrop.DragOverEvent, OnBoardDragOver);
        cardBorder.AddHandler(DragDrop.DragLeaveEvent, OnBoardDragLeave);
        cardBorder.AddHandler(DragDrop.DropEvent, OnBoardDrop);

        // Insertion lines at the card's top/bottom edges, bound to the same node
        // flags the tree rows bind, so a drag shows exactly where the card lands.
        var cardWrapper = new Grid();
        cardWrapper.Children.Add(cardBorder);
        cardWrapper.Children.Add(CreateBoardDropLine(horizontal: true, atStart: true, nameof(KastnTreeNode.ShowDropBefore)));
        cardWrapper.Children.Add(CreateBoardDropLine(horizontal: true, atStart: false, nameof(KastnTreeNode.ShowDropAfter)));

        return new BoardCardUi
        {
            Wrapper = cardWrapper,
            CardBorder = cardBorder,
            RenderKey = renderKey,
            PendingPictureLoad = pendingPictureLoad
        };
    }

    private async Task EditBoardSlipAsync(ZetlSlipSnapshot slip)
    {
        if (currentProject is null || !IsOnline)
        {
            return;
        }

        // Open the dialog
        var result = await KastnDialogs.EditSlipDialogAsync(this, slip, currentProject.Buckets);
        if (result is null)
        {
            return; // Cancelled
        }

        if (result.Delete)
        {
            // Execute DeleteSlip command
            var deleteResponse = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.DeleteSlip,
                new DeleteSlipCommand(),
                currentProject.Id,
                slip.Id,
                slip.Revision));

            if (deleteResponse.Status == ZetlResponseStatus.Success)
            {
                // If the deleted slip was selected in editor, deselect it
                if (string.Equals(slip.Id, editorState.SlipId, StringComparison.Ordinal))
                {
                    editorState.Select(null);
                    UpdateEditorFromState();
                }
                await connection.RefreshAsync();
                statusText.Text = "Slip moved to Deleted.";
            }
            else
            {
                await KastnDialogs.MessageAsync(this, "Delete Failed", deleteResponse.Error?.Message ?? $"Failed to delete slip: {deleteResponse.Status}.");
            }
            return;
        }

        if (result.Save)
        {
            // A bucket move plus content update for one slip is a single undo step.
            using var undoGesture = BeginGesture("Edit slip");
            // Update slip content and column/bucket and block kind
            var revision = string.Equals(slip.Id, editorState.SlipId, StringComparison.Ordinal)
                ? editorState.Revision
                : slip.Revision;

            // Send MoveSlip if bucket changed
            if (result.DestinationBucketId is not null && result.DestinationBucketId != slip.BucketId)
            {
                var moveResponse = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                    Guid.NewGuid().ToString("N"),
                    ZetlCommandKind.MoveSlip,
                    new MoveSlipCommand { DestinationBucketId = result.DestinationBucketId },
                    currentProject.Id,
                    slip.Id,
                    revision));

                if (moveResponse.Status != ZetlResponseStatus.Success)
                {
                    await KastnDialogs.MessageAsync(this, "Move Failed", moveResponse.Error?.Message ?? $"Failed to move slip: {moveResponse.Status}.");
                    return;
                }

                revision = moveResponse.Payload?.Deserialize<ZetlSlipSnapshot>(
                    ZetlProtocolJson.Options)?.Revision ?? revision;
            }

            // Send UpdateSlip command for Text and BlockKind
            var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.UpdateSlip,
                new UpdateSlipCommand
                {
                    Text = result.Text.Trim(),
                    BlockKind = result.BlockKind,
                    IgnoreBucketRenderKind = result.IgnoreBucketRenderKind
                },
                currentProject.Id,
                slip.Id,
                revision));

            if (response.Status == ZetlResponseStatus.Success)
            {
                // If editing the active slip in the details editor, update the editor state too
                if (string.Equals(slip.Id, editorState.SlipId, StringComparison.Ordinal))
                {
                    var saved = response.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options);
                    if (saved is not null)
                    {
                        editorState.AcceptSaved(saved);
                        UpdateEditorFromState();
                    }
                }

                await connection.RefreshAsync();
                statusText.Text = "Slip saved.";
            }
            else
            {
                await KastnDialogs.MessageAsync(this, "Save Failed", response.Error?.Message ?? $"Failed to save slip: {response.Status}.");
            }
        }
    }

    private void UpdateBoardSelectionHighlight(bool scrollIntoView = true)
    {
        if (highlightedBoardSlipId is not null
            && boardSlipCards.TryGetValue(highlightedBoardSlipId, out var previous))
        {
            previous.BorderBrush = ThemeBrush("ZetlBorderBrush");
        }

        highlightedBoardSlipId = null;
        var selectedId = SelectedTreeNode?.Slip?.Id ?? editorState.SlipId;
        if (selectedId is null || !boardSlipCards.TryGetValue(selectedId, out var card))
        {
            return;
        }

        card.BorderBrush = ThemeBrush("ZetlAccentBrush");
        highlightedBoardSlipId = selectedId;

        if (scrollIntoView)
        {
            card.BringIntoView();
            Dispatcher.UIThread.Post(card.BringIntoView, DispatcherPriority.Background);
        }
    }

    // Load a board card's thumbnail into the shared decoded-picture cache. A load
    // that outlives its card — the card was replaced while the bytes were in
    // flight — drops its assignment; the decoded bitmap stays cached either way,
    // so the replacement card picks it up synchronously.
    private async void LoadBoardCardPictureAsync(
        ZetlSlipSnapshot slip,
        BoardCardUi card,
        Avalonia.Controls.Image image,
        TextBlock status)
    {
        bool IsCurrent() =>
            boardCards.TryGetValue(slip.Id, out var current) && ReferenceEquals(current, card);

        try
        {
            var content = await GetPictureContentAsync(slip);
            if (!IsCurrent())
            {
                return;
            }

            if (content is null)
            {
                status.Text = "Picture unavailable.";
                return;
            }

            // 260: smaller decode width for board card thumbnails.
            var bitmap = DecodeAndCachePicture(content, slip.Picture!.Sha256, 260);
            if (!IsCurrent())
            {
                return;
            }

            image.Source = bitmap;
            status.IsVisible = false;
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (IsCurrent())
            {
                status.Text = "Picture unavailable.";
            }
        }
    }

    // ---- Creation types (template + default view) ----
}
