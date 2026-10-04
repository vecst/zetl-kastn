using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal sealed record KastnViewEditorControls(
    Button viewAllBucketsButton,
    Border viewAllBucketsPanel,
    Button viewCustomSectionsButton,
    StackPanel viewCustomSectionsPanel,
    TextBox viewDescriptionBox,
    StackPanel viewDocumentSettingsPanel,
    TextBlock viewEditorTitle,
    Button viewFormattedKindButton,
    Button viewHtmlKindButton,
    ComboBox viewKindBox,
    ComboBox viewListStyleBox,
    StackPanel viewLivePreviewPanel,
    TextBlock viewLivePreviewSummary,
    Button viewMarkdownKindButton,
    TextBox viewNameBox,
    CheckBox viewNumberHeadingsCheck,
    Button viewPdfKindButton,
    Button viewPlainKindButton,
    StackPanel viewSectionEditorPanel,
    CheckBox viewShowTitleCheck,
    TextBox viewTitleBox,
    Button viewTsvKindButton,
    StackPanel viewTsvPanel,
    NumericUpDown viewTsvRowBox,
    Button addViewSectionButton);

// Owns view authoring controls, section gestures, and bounded rich previews.
// Persistence and project navigation remain explicit window workflows.
internal sealed class KastnViewEditorPresenter
{
    private static readonly string[] ViewKindChoices =
        [ZetlViewKinds.Formatted, ZetlViewKinds.Plain, ZetlViewKinds.Tsv, ZetlViewKinds.Markdown, ZetlViewKinds.Html, ZetlViewKinds.Pdf];
    public KastnViewEditorDraft? State { get; private set; }
    private readonly Func<KastnViewEditorContext> context;
    private readonly Func<KastnSlipContentRenderer> createRenderer;
    private readonly Func<bool> preferSlipKinds;
    private bool updating;
    private long structureVersion;
    private string? previewSignature;
    private long previewVersion;
    private ZetlProjectSnapshot? PreviewProject => State is { } state && state.Context.Matches(context()) ? context().Project : null;
    private readonly Button viewAllBucketsButton;
    private readonly Border viewAllBucketsPanel;
    private readonly Button viewCustomSectionsButton;
    private readonly StackPanel viewCustomSectionsPanel;
    private readonly TextBox viewDescriptionBox;
    private readonly StackPanel viewDocumentSettingsPanel;
    private readonly TextBlock viewEditorTitle;
    private readonly Button viewFormattedKindButton;
    private readonly Button viewHtmlKindButton;
    private readonly ComboBox viewKindBox;
    private readonly ComboBox viewListStyleBox;
    private readonly StackPanel viewLivePreviewPanel;
    private readonly TextBlock viewLivePreviewSummary;
    private readonly Button viewMarkdownKindButton;
    private readonly TextBox viewNameBox;
    private readonly CheckBox viewNumberHeadingsCheck;
    private readonly Button viewPdfKindButton;
    private readonly Button viewPlainKindButton;
    private readonly StackPanel viewSectionEditorPanel;
    private readonly CheckBox viewShowTitleCheck;
    private readonly TextBox viewTitleBox;
    private readonly Button viewTsvKindButton;
    private readonly StackPanel viewTsvPanel;
    private readonly NumericUpDown viewTsvRowBox;
    private readonly Button addViewSectionButton;

    public KastnViewEditorPresenter(KastnViewEditorControls controls,
        Func<KastnViewEditorContext> context, Func<KastnSlipContentRenderer> createRenderer, Func<bool> preferSlipKinds)
    {
        this.context = context;
        this.createRenderer = createRenderer;
        this.preferSlipKinds = preferSlipKinds;
        viewAllBucketsButton = controls.viewAllBucketsButton;
        viewAllBucketsPanel = controls.viewAllBucketsPanel;
        viewCustomSectionsButton = controls.viewCustomSectionsButton;
        viewCustomSectionsPanel = controls.viewCustomSectionsPanel;
        viewDescriptionBox = controls.viewDescriptionBox;
        viewDocumentSettingsPanel = controls.viewDocumentSettingsPanel;
        viewEditorTitle = controls.viewEditorTitle;
        viewFormattedKindButton = controls.viewFormattedKindButton;
        viewHtmlKindButton = controls.viewHtmlKindButton;
        viewKindBox = controls.viewKindBox;
        viewListStyleBox = controls.viewListStyleBox;
        viewLivePreviewPanel = controls.viewLivePreviewPanel;
        viewLivePreviewSummary = controls.viewLivePreviewSummary;
        viewMarkdownKindButton = controls.viewMarkdownKindButton;
        viewNameBox = controls.viewNameBox;
        viewNumberHeadingsCheck = controls.viewNumberHeadingsCheck;
        viewPdfKindButton = controls.viewPdfKindButton;
        viewPlainKindButton = controls.viewPlainKindButton;
        viewSectionEditorPanel = controls.viewSectionEditorPanel;
        viewShowTitleCheck = controls.viewShowTitleCheck;
        viewTitleBox = controls.viewTitleBox;
        viewTsvKindButton = controls.viewTsvKindButton;
        viewTsvPanel = controls.viewTsvPanel;
        viewTsvRowBox = controls.viewTsvRowBox;
        addViewSectionButton = controls.addViewSectionButton;
        viewKindBox.ItemsSource = ViewKindChoices;
        viewListStyleBox.ItemsSource = ZetlViewListStyles.All;
        viewFormattedKindButton.Click += (_, _) => SetKind(ZetlViewKinds.Formatted);
        viewPlainKindButton.Click += (_, _) => SetKind(ZetlViewKinds.Plain);
        viewTsvKindButton.Click += (_, _) => SetKind(ZetlViewKinds.Tsv);
        viewMarkdownKindButton.Click += (_, _) => SetKind(ZetlViewKinds.Markdown);
        viewHtmlKindButton.Click += (_, _) => SetKind(ZetlViewKinds.Html);
        viewPdfKindButton.Click += (_, _) => SetKind(ZetlViewKinds.Pdf);
        viewAllBucketsButton.Click += (_, _) => SetViewStructureMode(false);
        viewCustomSectionsButton.Click += (_, _) => SetViewStructureMode(true);
        addViewSectionButton.Click += (_, _) => AddViewSection();
        viewNameBox.TextChanged += (_, _) => OnFieldsChanged();
        viewDescriptionBox.TextChanged += (_, _) => OnFieldsChanged();
        viewListStyleBox.SelectionChanged += (_, _) => OnFieldsChanged();
        viewNumberHeadingsCheck.IsCheckedChanged += (_, _) => OnFieldsChanged();
        viewTitleBox.TextChanged += (_, _) => OnFieldsChanged();
        viewShowTitleCheck.IsCheckedChanged += (_, _) => OnFieldsChanged();
        viewTsvRowBox.ValueChanged += (_, _) => OnFieldsChanged();
        viewKindBox.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            ApplyViewKindSettingsVisibility();
            OnFieldsChanged();
        };
    }

    public void Open(ZetlViewDocument working, bool isNew, bool projectScoped)
    {
        Close();
        State = new(working, projectScoped, context());
        updating = true;
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
        RefreshViewStructureEditor();
        updating = false;

        ApplyViewKindSettingsVisibility();
        OnFieldsChanged();
        State.SetBaseline();

        RefreshPreview();
    }

    public void Close()
    {
        State = null;
        structureVersion++;
        viewSectionDragCandidate = null;
        draggingViewSection = null;
        viewSectionDragInProgress = false;
        previewSignature = null;
        previewVersion++;
        viewSectionEditorPanel.Children.Clear();
        viewLivePreviewPanel.Children.Clear();
    }

    public ZetlViewDocument? Capture()
    {
        OnFieldsChanged();
        return State?.Capture();
    }

    private void OnFieldsChanged()
    {
        if (updating || State is not { } state) return;
        var doc = state.Document;
        doc.Name = viewNameBox.Text?.Trim() ?? "";
        doc.Description = viewDescriptionBox.Text?.Trim() ?? "";
        doc.Kind = viewKindBox.SelectedItem as string ?? ZetlViewKinds.Formatted;
        doc.TsvRowLength = (int)(viewTsvRowBox.Value ?? 5);
        doc.ListStyle = viewListStyleBox.SelectedItem as string ?? ZetlViewListStyles.Bullet;
        doc.NumberHeadings = viewNumberHeadingsCheck.IsChecked == true;
        doc.ShowTitle = viewShowTitleCheck.IsChecked == true;
        doc.Title = viewTitleBox.Text?.Trim() ?? "";
        RefreshPreview();
    }

    private void SetKind(string kind)
    {
        viewKindBox.SelectedItem = kind;
        ApplyViewKindSettingsVisibility();
        OnFieldsChanged();
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

    private static readonly DataFormat<string> ViewSectionDragFormat =
        DataFormat.CreateStringApplicationFormat("kastn-view-section");
    private KastnViewSectionDraft? viewSectionDragCandidate;
    private KastnViewSectionDraft? draggingViewSection;
    private Point viewSectionDragStart;
    private bool viewSectionDragInProgress;

    private void SetViewStructureMode(bool custom)
    {
        if (State is not { } state) return;
        state.SetStructureMode(custom, ProjectBucketNames());
        RefreshViewStructureEditor();
    }

    private void AddViewSection()
    {
        if (State is not { } state) return;
        state.AddSection(ProjectBucketNames());
        RefreshViewStructureEditor();
    }

    private void RefreshViewStructureEditor()
    {
        viewAllBucketsPanel.IsVisible = !State!.CustomSections;
        viewCustomSectionsPanel.IsVisible = State!.CustomSections;
        viewAllBucketsButton.Classes.Set("view-format-active", !State!.CustomSections);
        viewCustomSectionsButton.Classes.Set("view-format-active", State!.CustomSections);
        structureVersion++;
        viewSectionEditorPanel.Children.Clear();

        for (var index = 0; index < State!.Sections.Count; index++)
        {
            viewSectionEditorPanel.Children.Add(BuildViewSectionCard(State!.Sections[index], index));
        }

        RefreshPreview();
    }

    private Control BuildViewSectionCard(KastnViewSectionDraft draft, int index)
    {
        var session = State;
        var version = structureVersion;
        bool IsLive() => ReferenceEquals(State, session) && version == structureVersion;
        var titleBox = new TextBox
        {
            Text = draft.Title,
            Watermark = "Section heading"
        };
        titleBox.TextChanged += (_, _) =>
        {
            if (!IsLive()) return;
            draft.Title = titleBox.Text?.Trim() ?? "";
            RefreshPreview();
        };

        var moveUp = SmallSectionButton("↑", "Move section up");
        moveUp.IsEnabled = index > 0;
        moveUp.Click += (_, _) => { if (IsLive()) { State!.MoveSection(draft, -1); RefreshViewStructureEditor(); } };
        var moveDown = SmallSectionButton("↓", "Move section down");
        moveDown.IsEnabled = index < State!.Sections.Count - 1;
        moveDown.Click += (_, _) => { if (IsLive()) { State!.MoveSection(draft, 1); RefreshViewStructureEditor(); } };
        var remove = SmallSectionButton("Remove", "Remove section");
        remove.Click += (_, _) =>
        {
            if (!IsLive()) return;
            State!.RemoveSection(draft, ProjectBucketNames());
            RefreshViewStructureEditor();
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
        dragHandle.PointerPressed += (_, args) => { if (IsLive()) BeginViewSectionDragCandidate(draft, dragHandle, args); };
        dragHandle.PointerMoved += async (_, args) => { if (IsLive()) await ContinueViewSectionDragAsync(draft, dragHandle, args); };
        dragHandle.PointerReleased += (_, _) => { if (IsLive()) viewSectionDragCandidate = null; };

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
                if (!IsLive()) return;
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
                if (!IsLive()) return;
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
        content.Children.Add(BuildSectionHeadingStyleRow(draft, IsLive));
        var card = new Border
        {
            Classes = { "surface" },
            Padding = new Thickness(12),
            Child = content
        };
        DragDrop.SetAllowDrop(card, true);
        card.AddHandler(DragDrop.DragOverEvent, (_, args) => { if (IsLive()) OnViewSectionDragOver(draft, card, args); });
        card.AddHandler(DragDrop.DragLeaveEvent, (_, _) => { if (IsLive()) ClearViewSectionDropIndicator(card); });
        card.AddHandler(DragDrop.DropEvent, (_, args) => { if (IsLive()) OnViewSectionDrop(draft, card, args); });
        return card;
    }

    // Heading styling for a custom section: size (level), alignment, and bold. These
    // make the section title "look nicer" in the HTML / PDF / on-screen document
    // (Markdown keeps a plain heading, honoring only the size as its heading depth).
    private Control BuildSectionHeadingStyleRow(KastnViewSectionDraft draft, Func<bool> isLive)
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
            if (!isLive()) return;
            draft.HeadingLevel = sizeBox.SelectedIndex switch { 1 => 1, 2 => 3, _ => 2 };
            RefreshPreview();
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
            if (!isLive()) return;
            draft.HeadingAlign = alignBox.SelectedIndex switch { 1 => "center", 2 => "right", _ => "" };
            RefreshPreview();
        };

        var boldCheck = new CheckBox
        {
            Content = "Bold",
            IsChecked = draft.HeadingBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        boldCheck.IsCheckedChanged += (_, _) =>
        {
            if (!isLive()) return;
            draft.HeadingBold = boldCheck.IsChecked == true;
            RefreshPreview();
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
        KastnViewSectionDraft draft,
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
        KastnViewSectionDraft draft,
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
        var session = State;
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
            if (ReferenceEquals(State, session))
            {
                draggingViewSection = null;
                viewSectionDragInProgress = false;
            }
        }
    }

    private void OnViewSectionDragOver(KastnViewSectionDraft target, Border card, DragEventArgs args)
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

    private void OnViewSectionDrop(KastnViewSectionDraft target, Border card, DragEventArgs args)
    {
        ClearViewSectionDropIndicator(card);
        if (draggingViewSection is not { } source
            || source == target
            || !args.DataTransfer.Contains(ViewSectionDragFormat))
        {
            return;
        }

        if (State?.ReorderSection(source, target, args.GetPosition(card).Y < card.Bounds.Height / 2) != true) return;
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

    private IReadOnlyList<string> ProjectBucketNames() => PreviewProject?.Buckets
        .Where(bucket => !KastnWorkbench.IsDeletedBucket(bucket))
        .Select(bucket => bucket.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray() ?? [];

    public void RefreshPreview()
    {
        if (updating || State is null) return;
        var currentProject = PreviewProject;
        var view = State.Capture();
        var preferSlipKindOverBucketKind = preferSlipKinds();
        var signature = $"{currentProject?.Id}|{currentProject?.ChangeSequence}|{preferSlipKindOverBucketKind}|{KastnViewEditorDraft.Fingerprint(view)}";
        if (signature == previewSignature) return;
        previewSignature = signature;
        var version = ++previewVersion;
        var session = State;
        if (currentProject is null)
        {
            viewLivePreviewPanel.Children.Clear();
            viewLivePreviewSummary.Text = "Open the original project to preview this view.";
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
        var deleted = currentProject.Buckets.Where(KastnWorkbench.IsDeletedBucket).Select(bucket => bucket.Id).ToHashSet(StringComparer.Ordinal);
        var slips = currentProject.Slips
            .Where(slip => !deleted.Contains(slip.BucketId))
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

        var contentRenderer = createRenderer().WithActionGuard(() =>
            ReferenceEquals(State, session) && previewVersion == version && session.Context.Matches(context()));
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
                var listKinds = ZetlViewRenderer.ResolveListKinds(
                    currentProject, slip, preferSlipKindOverBucketKind);
                var marker = KastnSlipContentRenderer.OuterListMarker(listKinds.Outer, slip.Checked, ref orderedRun)
                    + KastnSlipContentRenderer.InnerListMarker(listKinds.Inner, slip.Checked);

                viewLivePreviewPanel.Children.Add(BuildViewPreviewSlip(slip, group.Depth, marker, contentRenderer));
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

    private static Control BuildViewPreviewSlip(ZetlSlipSnapshot slip, int depth, string marker,
        KastnSlipContentRenderer renderer)
    {
        StackPanel content;
        if (slip.Type == ZetlSlipType.Picture)
        {
            content = new StackPanel { Spacing = 4 };
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
            KastnSlipContentRenderer.AddPictureCaption(content, slip);
        }
        else
        {
            content = renderer.CreateTextContent(slip, spacing: 4).Panel;
        }

        var child = renderer.WithListMarker(content, slip, marker, preview: true);

        return new Border
        {
            Child = child,
            Padding = new Thickness(5, 4),
            Margin = new Thickness((depth + 1) * 10, 0, 0, 2)
        };
    }
}
