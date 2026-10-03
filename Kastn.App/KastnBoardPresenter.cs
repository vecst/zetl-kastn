using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal sealed record KastnBoardAppearance(IBrush? BorderBrush, IBrush? AccentBrush,
    IBrush? SurfaceBrush, IBrush? SurfaceAltBrush);
internal sealed record KastnBoardActions(Action<string> SelectSlip, Func<string, Task> EditSlip,
    Func<string, string, Task<bool>> AddSlip, Action<Control, Func<bool>> WireCard,
    Action<Control, Control, Func<bool>> WireColumn);
internal sealed record KastnBoardRenderInputs(KastnProjectIndex Index, IReadOnlyList<ZetlSlipSnapshot> Slips,
    KastnViewRenderKey Key, bool DeletedOnly, KastnSlipContentRenderer ContentRenderer,
    KastnBoardAppearance Appearance, Func<string, object?> NodeContext, KastnBoardActions Actions);

// Owns board controls, composer drafts and picture assignment. Native drag
// gestures and project mutations stay in the adapter; bitmaps stay cache-owned.
internal sealed class KastnBoardPresenter(StackPanel panel, ScrollViewer scroll, KastnPictureCache pictures) : IDisposable
{
    private KastnViewRenderKey? renderKey;
    private string? projectId;
    private string? highlightedBoardSlipId;
    private bool deletedOnly;
    private bool active;
    private bool disposed;
    private long selectionVersion;
    private KastnBoardAppearance appearance = new(null, null, null, null);
    public bool IsDisposed => disposed;
    public Border? Card(string id) => boardCards.GetValueOrDefault(id)?.CardBorder;
    public StackPanel? ColumnCards(string id) => boardColumns.GetValueOrDefault(id)?.CardsPanel;

    public void Render(KastnBoardRenderInputs inputs, string? selectedSlipId)
    {
        if (disposed) return;
        var sameProject = projectId == inputs.Index.Project.Id;
        if (!sameProject) Clear();
        var rebuilt = !active || renderKey != inputs.Key || deletedOnly != inputs.DeletedOnly;
        projectId = inputs.Index.Project.Id;
        active = true;
        appearance = inputs.Appearance;
        if (rebuilt)
        {
            var offset = scroll.Offset;
            var columnOffsets = boardColumns.Values.Select(column => (column.Scroll, column.Scroll.Offset)).ToArray();
            BuildBoardView(inputs);
            renderKey = inputs.Key;
            deletedOnly = inputs.DeletedOnly;
            scroll.UpdateLayout();
            if (sameProject)
            {
                RestoreScroll(scroll, offset);
                foreach (var (columnScroll, columnOffset) in columnOffsets) RestoreScroll(columnScroll, columnOffset);
            }
        }
        UpdateSelection(selectedSlipId, scrollIntoView: !rebuilt);
    }

    public void Suspend()
    {
        active = false;
        selectionVersion++;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Clear();
    }

    private bool IsLive(string id, BoardCardUi card) => !disposed
        && boardCards.TryGetValue(id, out var current) && ReferenceEquals(current, card);
    private bool IsLive(string id, BoardColumnUi column) => !disposed
        && boardColumns.TryGetValue(id, out var current) && ReferenceEquals(current, column);
    private bool CanAct(string id, BoardColumnUi column) => active && IsLive(id, column);
    private static void RestoreScroll(ScrollViewer viewer, Vector offset) => viewer.Offset = new Vector(
        Math.Clamp(offset.X, 0, Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width)),
        Math.Clamp(offset.Y, 0, Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height)));

    // Reconciled board state, keyed by bucket/slip id, so a refresh updates only
    // what changed instead of rebuilding the whole board (which flashed and reset
    // every scroll position). A card is reused while its render inputs are
    // unchanged; decoded picture bitmaps belong to the shared picture cache.
    private sealed class BoardColumnUi
    {
        public required Grid Wrapper { get; init; }
        public required TextBlock TitleText { get; init; }
        public required TextBlock CountText { get; init; }
        public required StackPanel CardsPanel { get; init; }
        public required ScrollViewer Scroll { get; init; }
        public long DraftVersion { get; set; }

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
        // (the decoded bitmap lives in the shared picture cache). The
        // reconcile starts the load after the card is registered, so a load that
        // completes synchronously still sees itself as the current card. A dual
        // card also feeds its click-to-peek expanded image from the same bitmap.
        public (Image Image, Image? ExpandedImage, TextBlock Status)? PendingPictureLoad { get; set; }
        public Task? PictureLoad { get; set; }
    }

    private readonly Dictionary<string, BoardColumnUi> boardColumns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BoardCardUi> boardCards = new(StringComparer.Ordinal);

    // Dual (text + picture) cards the user expanded to peek at the picture.
    // Keyed by slip id rather than stored on the card so a peek survives the
    // card rebuilds a revision bump causes; pruned with stale cards and cleared
    // with the board.
    private readonly HashSet<string> expandedBoardPictures = new(StringComparer.Ordinal);

    private void BuildBoardView(KastnBoardRenderInputs inputs)
    {
        var visible = inputs.Slips;
        // Traverse bucket snapshots directly: board columns need canonical bucket
        // order, not a second projection of every slip into temporary tree nodes.
        var buckets = inputs.Index.OrderedBuckets();
        var visibleSlipsByBucket = visible
            .Where(slip => !KastnWorkbench.IsDeletedBucket(inputs.Index.Bucket(slip.BucketId)))
            .GroupBy(slip => slip.BucketId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ZetlSlipSnapshot>)g.ToList(), StringComparer.Ordinal);

        var desiredColumns = new List<Control>();
        var liveBucketIds = new HashSet<string>(StringComparer.Ordinal);
        var liveSlipIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bucket in buckets)
        {
            liveBucketIds.Add(bucket.Id);
            var slips = visibleSlipsByBucket.TryGetValue(bucket.Id, out var found) ? found : [];
            if (!boardColumns.TryGetValue(bucket.Id, out var column))
            {
                column = CreateBoardColumn(bucket, inputs);
                boardColumns[bucket.Id] = column;
            }

            // Drag markers bind live node flags, so reused columns must adopt the
            // current projection's context (including changes to Deleted mode).
            column.TitleText.Text = bucket.Name;
            column.CountText.Text = $"({slips.Count})";
            column.Wrapper.DataContext =
                inputs.NodeContext(bucket.Id);

            ReconcileColumnCards(column, bucket, slips, liveSlipIds, inputs);
            desiredColumns.Add(column.Wrapper);
        }

        foreach (var staleId in boardColumns.Keys.Where(id => !liveBucketIds.Contains(id)).ToList())
        {
            boardColumns.Remove(staleId);
        }

        foreach (var staleId in boardCards.Keys.Where(id => !liveSlipIds.Contains(id)).ToList())
        {
            RemoveBoardCard(staleId);
            expandedBoardPictures.Remove(staleId);
        }

        KastnPanelReconciler.SyncChildren(panel.Children, desiredColumns);
    }

    public void Clear()
    {
        Suspend();
        renderKey = null;
        projectId = null;
        boardCards.Clear();
        boardColumns.Clear();
        panel.Children.Clear();
        expandedBoardPictures.Clear();
        highlightedBoardSlipId = null;
        scroll.Offset = Vector.Zero;
    }

    private void ReconcileColumnCards(
        BoardColumnUi column,
        ZetlBucketSnapshot bucket,
        IReadOnlyList<ZetlSlipSnapshot> slips,
        HashSet<string> liveSlipIds, KastnBoardRenderInputs inputs)
    {
        var desiredCards = new List<Control>();
        foreach (var slip in slips)
        {
            liveSlipIds.Add(slip.Id);
            var renderKey = BoardCardRenderKey(slip, bucket, inputs.Key);
            if (boardCards.TryGetValue(slip.Id, out var card)
                && !string.Equals(card.RenderKey, renderKey, StringComparison.Ordinal))
            {
                // Content changed: rebuild this one card.
                RemoveBoardCard(slip.Id);
                card = null;
            }

            if (card is null)
            {
                card = CreateBoardCard(slip, renderKey, inputs);
                boardCards[slip.Id] = card;
                if (card.PendingPictureLoad is { } load)
                {
                    card.PendingPictureLoad = null;
                    card.PictureLoad = LoadBoardCardPictureAsync(inputs.Index.Project.Id, slip, card, load.Image, load.ExpandedImage, load.Status);
                }
            }

            card.Wrapper.DataContext =
                inputs.NodeContext(slip.Id);
            desiredCards.Add(card.Wrapper);
        }

        KastnPanelReconciler.SyncChildren(column.CardsPanel.Children, desiredCards);
    }

    // The inputs a rendered card depends on beyond its position: the slip's own
    // revision plus the bucket/setting context that shapes its footer markers.
    private string BoardCardRenderKey(ZetlSlipSnapshot slip, ZetlBucketSnapshot bucket, KastnViewRenderKey key) =>
        $"{slip.Revision}|{bucket.RenderKind}|{key.PreferSlipKindOverBucketKind}|{key.UntitledSlipTitle}";

    private void RemoveBoardCard(string slipId)
    {
        if (!boardCards.TryGetValue(slipId, out var card))
        {
            return;
        }

        boardCards.Remove(slipId);
        if (card.Wrapper.Parent is Panel parent)
        {
            parent.Children.Remove(card.Wrapper);
        }
    }

    // The reusable column shell: header, cards panel, drag wiring, and drop
    // overlays. The reconcile pass owns the changing parts — header text, node
    // DataContext, and the card list.
    private BoardColumnUi CreateBoardColumn(ZetlBucketSnapshot bucket, KastnBoardRenderInputs inputs)
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
            Background = appearance.SurfaceAltBrush,
            BorderBrush = appearance.AccentBrush,
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
            Background = appearance.SurfaceBrush,
            BorderBrush = appearance.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Width = 280,
            Padding = new Thickness(8),
            Child = mainGrid,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        // Drag feedback overlays, bound to the same node flags the tree rows bind:
        // an accent outline while cards would drop into this column, and vertical
        // insertion lines at the left/right edges while a dragged column would land
        // before/after it.
        var columnWrapper = new Grid();
        columnWrapper.Children.Add(columnBorder);
        var intoOverlay = new Border
        {
            BorderBrush = appearance.AccentBrush,
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
            Scroll = scrollViewer,
            Composer = composer,
            ComposerBox = composerBox
        };

        inputs.Actions.WireColumn(headerGrid, columnBorder, () => CanAct(bucketId, column));
        composerBox.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty) column.DraftVersion++;
        };
        addCardButton.Click += (_, _) =>
        {
            if (!CanAct(bucketId, column)) return;
            composer.IsVisible = true;
            composerBox.Focus();
        };
        composerBox.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (!CanAct(bucketId, column)) return;
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseBoardComposer(column);
            }
            else if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                await CommitBoardComposerAsync(bucketId, column, inputs.Actions.AddSlip, keepOpen: true);
            }
        }, RoutingStrategies.Tunnel);
        composerBox.LostFocus += async (_, _) =>
        {
            if (!CanAct(bucketId, column) || column.ComposerBusy) return;
            await CommitBoardComposerAsync(bucketId, column, inputs.Actions.AddSlip, keepOpen: false);
        };

        return column;
    }

    private static void CloseBoardComposer(BoardColumnUi column)
    {
        column.DraftVersion++;
        column.ComposerBox.Text = "";
        column.Composer.IsVisible = false;
    }

    // Send the composer text as a new slip at the end of the bucket. Returns true
    // when the card was added (or there was nothing to add); the composer clears
    // and, for Enter-to-add, stays focused for the next card.
    private async Task<bool> CommitBoardComposerAsync(string bucketId, BoardColumnUi column,
        Func<string, string, Task<bool>> addSlip, bool keepOpen)
    {
        if (!CanAct(bucketId, column) || column.ComposerBusy) return false;
        var draft = column.ComposerBox.Text ?? "";
        var text = draft.Trim();
        if (text.Length == 0)
        {
            if (!keepOpen) CloseBoardComposer(column);
            return true;
        }
        var version = column.DraftVersion;
        column.ComposerBusy = true;
        try
        {
            if (!await addSlip(bucketId, text)) return false;
            // The submitted card may have succeeded while the user kept writing,
            // discarded/reopened the composer, or navigated to another project.
            if (!IsLive(bucketId, column) || column.DraftVersion != version || column.ComposerBox.Text != draft) return true;
            column.ComposerBox.Text = "";
            // Keep the existing focus. A late response must not pull focus back
            // from another column or from the reader after a mode switch.
            if (!keepOpen) CloseBoardComposer(column);
            return true;
        }
        finally { column.ComposerBusy = false; }
    }

    // An accent insertion line overlaying one edge of a board card or column,
    // visibility-bound to the node's drop-edge state: horizontal lines mark card
    // slots (top/bottom), vertical lines mark column slots (left/right).
    private Control CreateBoardDropLine(bool horizontal, bool atStart, string visibilityProperty)
    {
        var line = new Border
        {
            Background = appearance.AccentBrush,
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
    private BoardCardUi CreateBoardCard(ZetlSlipSnapshot slip, string renderKey, KastnBoardRenderInputs inputs)
    {
        BoardCardUi? result = null;
        bool CanUse() => active && result is not null && IsLive(slip.Id, result);
        var contentRenderer = inputs.ContentRenderer.WithActionGuard(CanUse);
        var previewText = KastnSlipContentRenderer.CreateBoardPreview(slip, inputs.Key.UntitledSlipTitle);

        var listKinds = ZetlViewRenderer.ResolveListKinds(
            inputs.Index.Project, slip, inputs.Key.PreferSlipKindOverBucketKind);

        // Footer layout (contains tags/markers/checkbox)
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 4, 0, 0)
        };

        void AddFooterMarker(string kind)
        {
            if (contentRenderer.CreateBoardListMarker(kind, slip) is { } marker)
                footer.Children.Add(marker);
        }

        AddFooterMarker(listKinds.Outer);
        AddFooterMarker(listKinds.Inner);

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

        // A text-presenting dual slip keeps its text as the card content and adds
        // a small thumbnail on the right; clicking the thumbnail peeks the
        // attached picture below the text. The peek is view state only — the
        // slip's preferred representation is unchanged.
        (Image Image, Image? ExpandedImage, TextBlock Status)? pendingPictureLoad = null;
        if (slip.Type != ZetlSlipType.Picture && slip.Picture is not null)
        {
            var thumbnail = new Image
            {
                MaxWidth = 44,
                MaxHeight = 44,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(thumbnail, "Show the attached picture");
            previewText.Margin = new Thickness(0, 0, 6, 4);
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(previewText, 0);
            Grid.SetColumn(thumbnail, 1);
            header.Children.Add(previewText);
            header.Children.Add(thumbnail);
            mainPanel.Children.Add(header);

            var expandedImage = new Image
            {
                MaxWidth = 240,
                MaxHeight = 160,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsVisible = expandedBoardPictures.Contains(slip.Id)
            };
            var pictureStatus = new TextBlock
            {
                Text = "Loading...",
                Classes = { "muted" },
                FontSize = 11,
                IsVisible = false
            };
            mainPanel.Children.Add(expandedImage);
            mainPanel.Children.Add(pictureStatus);

            thumbnail.PointerPressed += (_, args) =>
            {
                args.Handled = true;
                if (!CanUse()) return;
                var expanded = !expandedBoardPictures.Remove(slip.Id);
                if (expanded)
                {
                    expandedBoardPictures.Add(slip.Id);
                }

                expandedImage.IsVisible = expanded;
            };

            if (pictures.FindDecoded(slip.Picture.Sha256, 260) is { } cachedDual)
            {
                thumbnail.Source = cachedDual;
                expandedImage.Source = cachedDual;
            }
            else
            {
                pendingPictureLoad = (thumbnail, expandedImage, pictureStatus);
            }
        }
        else
        {
            mainPanel.Children.Add(previewText);
        }

        // Start loading once the card exists so retired-card guards can reject
        // stale assignments. Decoded bitmaps remain owned by KastnPictureCache.
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
            if (pictures.FindDecoded(slip.Picture.Sha256, 260) is { } cachedThumbnail)
            {
                image.Source = cachedThumbnail;
                statusText.IsVisible = false;
            }
            else
            {
                pendingPictureLoad = (image, null, statusText);
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
            Background = appearance.SurfaceAltBrush,
            BorderBrush = appearance.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Child = mainPanel,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        cardBorder.PointerPressed += (_, args) =>
        {
            if (!args.Handled && CanUse()) inputs.Actions.SelectSlip(slip.Id);
        };
        inputs.Actions.WireCard(cardBorder, CanUse);
        cardBorder.DoubleTapped += async (_, _) =>
        {
            if (CanUse()) await inputs.Actions.EditSlip(slip.Id);
        };

        // Insertion lines at the card's top/bottom edges, bound to the same node
        // flags the tree rows bind, so a drag shows exactly where the card lands.
        var cardWrapper = new Grid();
        cardWrapper.Children.Add(cardBorder);
        cardWrapper.Children.Add(CreateBoardDropLine(horizontal: true, atStart: true, nameof(KastnTreeNode.ShowDropBefore)));
        cardWrapper.Children.Add(CreateBoardDropLine(horizontal: true, atStart: false, nameof(KastnTreeNode.ShowDropAfter)));

        result = new BoardCardUi
        {
            Wrapper = cardWrapper,
            CardBorder = cardBorder,
            RenderKey = renderKey,
            PendingPictureLoad = pendingPictureLoad
        };
        return result;
    }

    public void UpdateSelection(string? selectedId, bool scrollIntoView = true)
    {
        if (!active || disposed) return;
        var version = ++selectionVersion;
        if (highlightedBoardSlipId is not null && boardCards.TryGetValue(highlightedBoardSlipId, out var previous))
            previous.CardBorder.BorderBrush = appearance.BorderBrush;
        highlightedBoardSlipId = null;
        if (selectedId is null || !boardCards.TryGetValue(selectedId, out var card)) return;
        card.CardBorder.BorderBrush = appearance.AccentBrush;
        highlightedBoardSlipId = selectedId;
        if (!scrollIntoView) return;
        card.CardBorder.BringIntoView();
        Dispatcher.UIThread.Post(() =>
        {
            if (active && selectionVersion == version && highlightedBoardSlipId == selectedId && IsLive(selectedId, card))
                card.CardBorder.BringIntoView();
        }, DispatcherPriority.Background);
    }

    // Load a board card's thumbnail into the shared decoded-picture cache. A load
    // that outlives its card drops its assignment. A surviving replacement can
    // share the content fetch; decoded bitmaps remain owned by the shared cache.
    private async Task LoadBoardCardPictureAsync(
        string capturedProjectId, ZetlSlipSnapshot slip,
        BoardCardUi card,
        Avalonia.Controls.Image image,
        Avalonia.Controls.Image? expandedImage,
        TextBlock status)
    {
        bool IsCurrent() =>
            projectId == capturedProjectId && IsLive(slip.Id, card);

        // A dual card's status starts hidden (its thumbnail is a side affordance,
        // not the card content), so a failure must reveal it to be seen.
        void ReportUnavailable()
        {
            status.Text = "Picture unavailable.";
            status.IsVisible = true;
        }

        try
        {
            var content = await pictures.GetContentAsync(capturedProjectId, slip);
            if (!IsCurrent())
            {
                return;
            }

            if (content is null)
            {
                ReportUnavailable();
                return;
            }

            // 260: smaller decode width for board card thumbnails.
            var bitmap = pictures.Decode(content, 260);
            if (!IsCurrent())
            {
                return;
            }

            image.Source = bitmap;
            if (expandedImage is not null)
            {
                expandedImage.Source = bitmap;
            }

            status.IsVisible = false;
        }
        catch (Exception ex) when (
            KastnPictureCache.IsLoadFailure(ex))
        {
            if (IsCurrent())
            {
                ReportUnavailable();
            }
        }
    }

}
