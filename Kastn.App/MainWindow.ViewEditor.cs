using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    private sealed class ViewSectionDraft
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public string Title { get; set; } = "";

        public List<string> Buckets { get; } = [];

        public string HeadingAlign { get; set; } = "";

        public bool HeadingBold { get; set; }

        // 0 = automatic; 1/2/3 = large/normal/small.
        public int HeadingLevel { get; set; }
    }

    private readonly List<ViewSectionDraft> viewSectionDrafts = [];
    private static readonly DataFormat<string> ViewSectionDragFormat =
        DataFormat.CreateStringApplicationFormat("kastn-view-section");
    private ViewSectionDraft? viewSectionDragCandidate;
    private ViewSectionDraft? draggingViewSection;
    private Point viewSectionDragStart;
    private bool viewSectionDragInProgress;
    private bool viewUsesCustomSections;

    private void LoadViewStructureEditor(IReadOnlyList<ZetlViewSection> sections)
    {
        viewSectionDrafts.Clear();
        foreach (var section in sections)
        {
            var draft = new ViewSectionDraft
            {
                Title = section.Title,
                HeadingAlign = section.HeadingAlign,
                HeadingBold = section.HeadingBold,
                HeadingLevel = section.HeadingLevel
            };
            draft.Buckets.AddRange(section.Buckets);
            viewSectionDrafts.Add(draft);
        }

        viewUsesCustomSections = viewSectionDrafts.Count > 0;
        RefreshViewStructureEditor();
    }

    private void SetViewStructureMode(bool custom)
    {
        viewUsesCustomSections = custom;
        if (custom && viewSectionDrafts.Count == 0)
        {
            AddViewSection();
            return;
        }

        RefreshViewStructureEditor();
    }

    private void AddViewSection()
    {
        var bucket = ProjectBucketNames()
            .FirstOrDefault(name => viewSectionDrafts.All(section =>
                !section.Buckets.Contains(name, StringComparer.OrdinalIgnoreCase)))
            ?? ProjectBucketNames().FirstOrDefault();
        var draft = new ViewSectionDraft
        {
            Title = bucket ?? $"Section {viewSectionDrafts.Count + 1}"
        };
        if (bucket is not null)
        {
            draft.Buckets.Add(bucket);
        }

        viewSectionDrafts.Add(draft);
        viewUsesCustomSections = true;
        RefreshViewStructureEditor();
    }

    private void RefreshViewStructureEditor()
    {
        viewAllBucketsPanel.IsVisible = !viewUsesCustomSections;
        viewCustomSectionsPanel.IsVisible = viewUsesCustomSections;
        viewAllBucketsButton.Classes.Set("view-format-active", !viewUsesCustomSections);
        viewCustomSectionsButton.Classes.Set("view-format-active", viewUsesCustomSections);
        viewSectionEditorPanel.Children.Clear();

        for (var index = 0; index < viewSectionDrafts.Count; index++)
        {
            viewSectionEditorPanel.Children.Add(BuildViewSectionCard(viewSectionDrafts[index], index));
        }

        RefreshViewLivePreview();
    }

    private Control BuildViewSectionCard(ViewSectionDraft draft, int index)
    {
        var titleBox = new TextBox
        {
            Text = draft.Title,
            Watermark = "Section heading"
        };
        titleBox.TextChanged += (_, _) =>
        {
            draft.Title = titleBox.Text?.Trim() ?? "";
            RefreshViewLivePreview();
        };

        var moveUp = SmallSectionButton("↑", "Move section up");
        moveUp.IsEnabled = index > 0;
        moveUp.Click += (_, _) => MoveViewSection(index, -1);
        var moveDown = SmallSectionButton("↓", "Move section down");
        moveDown.IsEnabled = index < viewSectionDrafts.Count - 1;
        moveDown.Click += (_, _) => MoveViewSection(index, 1);
        var remove = SmallSectionButton("Remove", "Remove section");
        remove.Click += (_, _) =>
        {
            viewSectionDrafts.Remove(draft);
            if (viewSectionDrafts.Count == 0)
            {
                AddViewSection();
            }
            else
            {
                RefreshViewStructureEditor();
            }
        };

        var dragHandle = new Border
        {
            Padding = new Thickness(5, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new TextBlock
            {
                Text = "::",
                FontWeight = FontWeight.Bold,
                Classes = { "muted" }
            }
        };
        ToolTip.SetTip(dragHandle, "Drag to reorder this section");
        dragHandle.PointerPressed += (_, args) => BeginViewSectionDragCandidate(draft, dragHandle, args);
        dragHandle.PointerMoved += async (_, args) => await ContinueViewSectionDragAsync(draft, dragHandle, args);
        dragHandle.PointerReleased += (_, _) => viewSectionDragCandidate = null;

        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,6,*,6,Auto,4,Auto,8,Auto")
        };
        heading.Children.Add(dragHandle);
        Grid.SetColumn(titleBox, 2);
        heading.Children.Add(titleBox);
        Grid.SetColumn(moveUp, 4);
        heading.Children.Add(moveUp);
        Grid.SetColumn(moveDown, 6);
        heading.Children.Add(moveDown);
        Grid.SetColumn(remove, 8);
        heading.Children.Add(remove);

        var bucketRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 6,
            LineSpacing = 6
        };
        foreach (var bucket in draft.Buckets.ToArray())
        {
            var chip = new Button
            {
                Content = $"{bucket}  ×",
                Padding = new Thickness(9, 3)
            };
            ToolTip.SetTip(chip, $"Remove {bucket} from this section");
            chip.Click += (_, _) =>
            {
                draft.Buckets.Remove(bucket);
                RefreshViewStructureEditor();
            };
            bucketRow.Children.Add(chip);
        }

        var available = ProjectBucketNames()
            .Where(name => !draft.Buckets.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (available.Length > 0)
        {
            var picker = new ComboBox
            {
                ItemsSource = available,
                SelectedIndex = 0,
                MinWidth = 150,
                PlaceholderText = "Choose bucket"
            };
            var addBucket = new Button
            {
                Content = "+ Bucket",
                Padding = new Thickness(9, 3)
            };
            addBucket.Click += (_, _) =>
            {
                if (picker.SelectedItem is string bucket)
                {
                    draft.Buckets.Add(bucket);
                    RefreshViewStructureEditor();
                }
            };
            bucketRow.Children.Add(picker);
            bucketRow.Children.Add(addBucket);
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(heading);
        content.Children.Add(bucketRow);
        content.Children.Add(BuildSectionHeadingStyleRow(draft));
        var card = new Border
        {
            Classes = { "surface" },
            Padding = new Thickness(12),
            Child = content
        };
        DragDrop.SetAllowDrop(card, true);
        card.AddHandler(DragDrop.DragOverEvent, (_, args) => OnViewSectionDragOver(draft, card, args));
        card.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearViewSectionDropIndicator(card));
        card.AddHandler(DragDrop.DropEvent, (_, args) => OnViewSectionDrop(draft, card, args));
        return card;
    }

    // Heading styling for a custom section: size (level), alignment, and bold. These
    // make the section title "look nicer" in the HTML / PDF / on-screen document
    // (Markdown keeps a plain heading, honoring only the size as its heading depth).
    private Control BuildSectionHeadingStyleRow(ViewSectionDraft draft)
    {
        var sizeBox = new ComboBox
        {
            ItemsSource = new[] { "Normal size", "Large", "Small" },
            MinWidth = 120,
            SelectedIndex = draft.HeadingLevel switch { 1 => 1, 3 => 2, _ => 0 }
        };
        ToolTip.SetTip(sizeBox, "Heading size");
        sizeBox.SelectionChanged += (_, _) =>
        {
            draft.HeadingLevel = sizeBox.SelectedIndex switch { 1 => 1, 2 => 3, _ => 2 };
            RefreshViewLivePreview();
        };

        var alignBox = new ComboBox
        {
            ItemsSource = new[] { "Left", "Center", "Right" },
            MinWidth = 95,
            SelectedIndex = draft.HeadingAlign switch { "center" => 1, "right" => 2, _ => 0 }
        };
        ToolTip.SetTip(alignBox, "Heading alignment");
        alignBox.SelectionChanged += (_, _) =>
        {
            draft.HeadingAlign = alignBox.SelectedIndex switch { 1 => "center", 2 => "right", _ => "" };
            RefreshViewLivePreview();
        };

        var boldCheck = new CheckBox
        {
            Content = "Bold",
            IsChecked = draft.HeadingBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        boldCheck.IsCheckedChanged += (_, _) =>
        {
            draft.HeadingBold = boldCheck.IsChecked == true;
            RefreshViewLivePreview();
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "Heading",
                    Classes = { "muted" },
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                },
                sizeBox,
                alignBox,
                boldCheck
            }
        };
    }

    private void BeginViewSectionDragCandidate(
        ViewSectionDraft draft,
        Control handle,
        PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        viewSectionDragCandidate = draft;
        viewSectionDragStart = args.GetPosition(handle);
    }

    private async Task ContinueViewSectionDragAsync(
        ViewSectionDraft draft,
        Control handle,
        PointerEventArgs args)
    {
        if (viewSectionDragCandidate != draft || viewSectionDragInProgress)
        {
            return;
        }

        if (!args.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
        {
            viewSectionDragCandidate = null;
            return;
        }

        var point = args.GetPosition(handle);
        if (Math.Abs(point.X - viewSectionDragStart.X) < 4
            && Math.Abs(point.Y - viewSectionDragStart.Y) < 4)
        {
            return;
        }

        viewSectionDragCandidate = null;
        draggingViewSection = draft;
        viewSectionDragInProgress = true;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(ViewSectionDragFormat, draft.Id));
            await DragDrop.DoDragDropAsync(args, data, DragDropEffects.Move);
        }
        finally
        {
            draggingViewSection = null;
            viewSectionDragInProgress = false;
        }
    }

    private void OnViewSectionDragOver(ViewSectionDraft target, Border card, DragEventArgs args)
    {
        if (draggingViewSection is null
            || draggingViewSection == target
            || !args.DataTransfer.Contains(ViewSectionDragFormat))
        {
            args.DragEffects = DragDropEffects.None;
            ClearViewSectionDropIndicator(card);
            return;
        }

        var before = args.GetPosition(card).Y < card.Bounds.Height / 2;
        card.BorderBrush = new SolidColorBrush(Color.FromRgb(139, 124, 246));
        card.BorderThickness = before
            ? new Thickness(1, 4, 1, 1)
            : new Thickness(1, 1, 1, 4);
        args.DragEffects = DragDropEffects.Move;
        args.Handled = true;
    }

    private void OnViewSectionDrop(ViewSectionDraft target, Border card, DragEventArgs args)
    {
        ClearViewSectionDropIndicator(card);
        if (draggingViewSection is not { } source
            || source == target
            || !args.DataTransfer.Contains(ViewSectionDragFormat))
        {
            return;
        }

        var sourceIndex = viewSectionDrafts.IndexOf(source);
        var targetIndex = viewSectionDrafts.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0)
        {
            return;
        }

        var insertIndex = args.GetPosition(card).Y < card.Bounds.Height / 2
            ? targetIndex
            : targetIndex + 1;
        viewSectionDrafts.RemoveAt(sourceIndex);
        if (sourceIndex < insertIndex)
        {
            insertIndex--;
        }

        viewSectionDrafts.Insert(Math.Clamp(insertIndex, 0, viewSectionDrafts.Count), source);
        args.Handled = true;
        RefreshViewStructureEditor();
    }

    private static void ClearViewSectionDropIndicator(Border card)
    {
        card.ClearValue(Border.BorderBrushProperty);
        card.ClearValue(Border.BorderThicknessProperty);
    }

    private static Button SmallSectionButton(string content, string tip)
    {
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(9, 3),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        return button;
    }

    private void MoveViewSection(int index, int delta)
    {
        var destination = index + delta;
        if (index < 0 || destination < 0 || destination >= viewSectionDrafts.Count)
        {
            return;
        }

        (viewSectionDrafts[index], viewSectionDrafts[destination]) =
            (viewSectionDrafts[destination], viewSectionDrafts[index]);
        RefreshViewStructureEditor();
    }

    private IReadOnlyList<string> ProjectBucketNames() => currentProject?.Buckets
        .Where(bucket => !KastnWorkbench.IsDeletedBucket(bucket))
        .Select(bucket => bucket.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray() ?? [];

    private List<ZetlViewSection> CurrentViewSections()
    {
        if (!viewUsesCustomSections)
        {
            return [];
        }

        return viewSectionDrafts.Select(draft => new ZetlViewSection
        {
            Title = draft.Title.Trim(),
            Buckets = draft.Buckets.ToList(),
            HeadingAlign = draft.HeadingAlign,
            HeadingBold = draft.HeadingBold,
            HeadingLevel = draft.HeadingLevel
        }).ToList();
    }

    private void RefreshViewLivePreview()
    {
        if (editingView is null || currentProject is null || viewLivePreviewPanel is null)
        {
            return;
        }

        var view = CurrentViewDocument();
        if (view is null)
        {
            return;
        }

        viewLivePreviewPanel.Children.Clear();
        var viewLabel = string.IsNullOrWhiteSpace(view.Name) ? "Untitled view" : view.Name;
        viewLivePreviewSummary.Text = $"{view.Kind} · {viewLabel} · unsaved";
        // Honor the view's title control (hide / custom) the same way the rendered
        // document does, so the preview reflects what Copy/Export will produce.
        if (ZetlViewRenderer.DocumentTitle(currentProject, view) is { } documentTitle)
        {
            viewLivePreviewPanel.Children.Add(new TextBlock
            {
                Text = documentTitle,
                FontSize = 22,
                FontWeight = FontWeight.Bold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
        }

        const int previewLimit = 100;
        var slips = currentProject.Slips
            .Where(slip => !IsSlipInDeleted(slip))
            .Take(previewLimit)
            .ToList();
        var groups = ZetlViewRenderer.BuildGroups(currentProject, slips, view);
        if (groups.Count == 0)
        {
            viewLivePreviewPanel.Children.Add(new TextBlock
            {
                Text = "This structure currently renders no slips.",
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var group in groups)
        {
            viewLivePreviewPanel.Children.Add(new TextBlock
            {
                Text = ZetlViewRenderer.HeadingText(group, view),
                FontSize = Math.Max(13, 24 - (3 * group.EffectiveLevel)),
                FontWeight = group.HeadingBold ? FontWeight.Bold : FontWeight.SemiBold,
                TextAlignment = ZetlViewRenderer.NormalizeHeadingAlign(group.HeadingAlign) switch
                {
                    "center" => TextAlignment.Center,
                    "right" => TextAlignment.Right,
                    _ => TextAlignment.Left,
                },
                Margin = new Thickness(group.Depth * 10, 8, 0, 2),
                TextWrapping = TextWrapping.Wrap
            });

            // Each note carries its own list kind; ordered notes count over their run.
            var orderedRun = 0;
            foreach (var slip in group.Slips)
            {
                var slipKind = slip.Type == ZetlSlipType.Picture
                    ? ""
                    : ZetlViewRenderer.SlipBlockKind(slip);
                var preferSlipKindOverBucketKind = CurrentAppSettings().KastnPreferSlipKindOverBucketKind;
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

                viewLivePreviewPanel.Children.Add(BuildViewPreviewSlip(slip, group.Depth, marker));
            }
        }

        if (currentProject.Slips.Count > previewLimit)
        {
            viewLivePreviewPanel.Children.Add(new TextBlock
            {
                Text = $"Preview limited to the first {previewLimit} slips.",
                Classes = { "muted" },
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0)
            });
        }
    }

    private Control BuildViewPreviewSlip(ZetlSlipSnapshot slip, int depth, string marker)
    {
        var content = new StackPanel { Spacing = 4 };
        if (slip.Type == ZetlSlipType.Picture)
        {
            content.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(35, 139, 124, 246)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 24),
                Child = new TextBlock
                {
                    Text = $"Picture · {slip.Picture?.Width ?? 0} × {slip.Picture?.Height ?? 0}",
                    Classes = { "muted" },
                    HorizontalAlignment = HorizontalAlignment.Center
                }
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
        }
        else
        {
            var text = string.IsNullOrWhiteSpace(slip.Text) ? slip.Title : slip.Text;
            AppendSlipBlocks(content, slip, text.Trim());
        }

        Control child = content;
        if (marker.Length > 0)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 5
            };
            var markerBlock = new TextBlock { Text = marker, MinWidth = 16 };
            Grid.SetColumn(content, 1);
            row.Children.Add(markerBlock);
            row.Children.Add(content);
            child = row;
        }

        return new Border
        {
            Child = child,
            Padding = new Thickness(5, 4),
            Margin = new Thickness((depth + 1) * 10, 0, 0, 2)
        };
    }
}
