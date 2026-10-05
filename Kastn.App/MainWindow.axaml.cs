using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow : Window
{
    private readonly KastnSettings settings;
    private readonly KastnProjectCreationWorkflow projectCreation;
    private string UntitledSlipTitle => settings.Current.UntitledSlipTitle;

    private readonly KastnConnectionController connection;
    // Built-ins plus user templates; corrupt/invalid user files are skipped with a
    // diagnostic written to Console.Error (Kastn's existing diagnostic channel).
    private readonly KastnTemplateCatalog templateCatalog;
    private readonly KastnTemplateEditorPresenter templateEditor;
    private readonly KastnCreationEditorPresenter creationEditor;
    private readonly KastnViewCatalog viewCatalog;
    private readonly KastnViewPersistence viewPersistence;
    private readonly KastnViewEditorPresenter viewEditor;
    private string lastRenderedViewText = "";
    private bool viewWriteInProgress;
    // Kastn-owned runtime state (last project opened) for the startup preference.
    private readonly KastnStateStore stateStore;
    // One local editor draft, separate from authoritative Zetl project state.
    private readonly KastnDraftStore draftStore;
    // Creation types: bundle a template with a default view.
    private readonly ZetlCreationTypeStore creationStore;
    // Sentinel for the creation editor's "no view" choice.
    private static readonly ZetlViewDocument NoView = new() { Id = "", Name = "(no view)" };
    private readonly ObservableCollection<FilterItem> sources = [];
    private readonly ObservableCollection<FilterItem> sessions = [];
    private readonly ObservableCollection<DateFilterItem> dates = [];
    private readonly ObservableCollection<KastnBucketItem> parentBuckets = [];
    private readonly ObservableCollection<KastnBucketItem> moveBuckets = [];
    private readonly List<KastnRenderKindItem> bucketRenderKinds =
    [
        new KastnRenderKindItem("", "Standard (Notes)"),
        new KastnRenderKindItem("task", "Checklist"),
        new KastnRenderKindItem("bullet", "Bullet List"),
        new KastnRenderKindItem("ordered", "Numbered List"),
        new KastnRenderKindItem("group", "Group Box"),
        new KastnRenderKindItem("table", "Table"),
        new KastnRenderKindItem("latex", "LaTeX Block")
    ];
    private static readonly IReadOnlyList<KastnFontFamilyItem> FontFamilyChoices =
    [
        new("", "Default font"),
        new("Arial", "Arial"),
        new("Calibri", "Calibri"),
        new("Georgia", "Georgia"),
        new("Segoe UI", "Segoe UI"),
        new("Times New Roman", "Times New Roman"),
        new("Verdana", "Verdana"),
        new("Consolas", "Consolas"),
        new("Courier New", "Courier New")
    ];
    private static readonly IReadOnlyList<KastnFontSizeItem> FontSizeChoices =
    [
        new(0, "Default size"),
        new(9, "9 pt"),
        new(10, "10 pt"),
        new(11, "11 pt"),
        new(12, "12 pt"),
        new(14, "14 pt"),
        new(16, "16 pt"),
        new(18, "18 pt"),
        new(20, "20 pt"),
        new(24, "24 pt"),
        new(28, "28 pt"),
        new(32, "32 pt"),
        new(36, "36 pt"),
        new(48, "48 pt"),
        new(64, "64 pt"),
        new(72, "72 pt")
    ];
    private static readonly IReadOnlyList<KastnTextColorItem> TextColorChoices =
    [
        new("", "Default", Brushes.Transparent),
        new("#000000", "Black", new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00))),
        new("#6B7280", "Gray", new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))),
        new("#DC2626", "Red", new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26))),
        new("#EA580C", "Orange", new SolidColorBrush(Color.FromRgb(0xEA, 0x58, 0x0C))),
        new("#CA8A04", "Gold", new SolidColorBrush(Color.FromRgb(0xCA, 0x8A, 0x04))),
        new("#16A34A", "Green", new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A))),
        new("#2563EB", "Blue", new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB))),
        new("#9333EA", "Purple", new SolidColorBrush(Color.FromRgb(0x93, 0x33, 0xEA))),
        new("#FFFFFF", "White", new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)))
    ];
    internal readonly KastnEditorState editorState = new();
    private readonly KastnPictureCache pictureCache;
    private readonly KastnReaderPresenter readerPresenter;
    private readonly KastnBoardPresenter boardPresenter;
    private readonly KastnInspectorPresenter inspectorPresenter;
    private ZetlProjectSnapshot? currentProject;
    private KastnProjectIndex? projectIndex;
    private KastnProjectIndex ProjectIndex
    {
        get
        {
            var project = currentProject ?? throw new InvalidOperationException("No project selected.");
            if (projectIndex is null || !ReferenceEquals(projectIndex.Project, project))
            {
                projectIndex = new KastnProjectIndex(project, projectIndex);
            }
            return projectIndex;
        }
    }
    internal readonly KastnTreeProjection treeProjection = new();
    private bool refreshing;
    private bool editorUpdating;
    private DispatcherTimer? draftJournalTimer;
    private string? restoredDraftKey;
    private bool recoveredDraftActive;
    private KastnEditorSaveOperation? pendingEditorSave;
    private KastnEditorMutationAcceptance? pendingEditorMutation;
    private readonly KastnNavigationCoordinator navigation;
    private bool detailShowingMetadata;
    private bool slipRenderOptionUpdating;
    private bool slipTypographyUpdating;
    // When true the project tree shows only the Deleted bucket's slips (browse +
    // restore), instead of the normal working tree.
    private bool showingDeleted;
    private bool boardModeActive;
    private GridLength treeColumnWidth = new GridLength(300, GridUnitType.Pixel);
    private GridLength leftSplitterWidth = new GridLength(8, GridUnitType.Pixel);
    private GridLength rightColumnWidth = new GridLength(360, GridUnitType.Pixel);
    private GridLength rightSplitterWidth = new GridLength(8, GridUnitType.Pixel);
    private bool bucketHeadingUpdating;
    private readonly KastnViewRenderCache viewRenderCache = new();
    private readonly KastnWindowLifetime lifetime;

    // Avalonia tooling needs a parameterless window without a live transport.
    public MainWindow() : this(null, null, null, null, null, null, null, initializeRuntime: false) { }

    public MainWindow(
        KastnConnectionController connection,
        KastnDraftStore? draftStore = null,
        ZetlViewStore? viewStore = null,
        KastnSettings? settings = null,
        ZetlTemplateStore? templateStore = null,
        ZetlCreationTypeStore? creationStore = null,
        KastnStateStore? stateStore = null)
        : this(connection, draftStore, viewStore, settings, templateStore, creationStore, stateStore, initializeRuntime: true) { }

    private MainWindow(
        KastnConnectionController? connection,
        KastnDraftStore? draftStore,
        ZetlViewStore? viewStore,
        KastnSettings? settings,
        ZetlTemplateStore? templateStore,
        ZetlCreationTypeStore? creationStore,
        KastnStateStore? stateStore,
        bool initializeRuntime)
    {
        if (initializeRuntime) ArgumentNullException.ThrowIfNull(connection);
        this.stateStore = stateStore ?? new(log: Console.Error.WriteLine);
        this.settings = settings ?? new();
        projectCreation = new(this.settings);
        this.connection = connection!;
        templateCatalog = new(templateStore ?? new ZetlTemplateStore(log: Console.Error.WriteLine));
        this.creationStore = creationStore ?? new(log: Console.Error.WriteLine);
        viewCatalog = new(viewStore ?? new ZetlViewStore(log: Console.Error.WriteLine));
        viewPersistence = new(viewCatalog.Store);
        editHistory = CreateEditHistory();
        pictureCache = new KastnPictureCache(FetchPictureContentAsync);
        this.draftStore = draftStore ?? new KastnDraftStore(log: Console.Error.WriteLine);
        InitializeComponent();
        templateEditor = CreateTemplateEditorPresenter();
        creationEditor = CreateCreationEditorPresenter();
        readerPresenter = new(viewerDocumentPanel, viewerDocumentScroll, pictureCache);
        boardPresenter = new(boardColumnsPanel, boardScrollViewer, pictureCache);
        inspectorPresenter = new(slipInspectorFieldsPanel, inspectorPanel);
        viewEditor = CreateViewEditorPresenter();
        lifetime = CreateWindowLifetime();
        mutations = new(OnMutationStateChanged);
        navigation = CreateNavigationCoordinator();
        snapshots = CreateSnapshotCoordinator();
        landing = CreateLandingPage();
        WireWindowLifetime();
        if (!initializeRuntime) return;

        Icon = KastnIcon.Create();
        InitializeControlChoices();
        WireControlEvents();
        ApplySnapshot(this.connection.Current);
    }

    public async Task ActivateRequestAsync(string? projectId)
    {
        if (!lifetime.CanActivate) return;
        lifetime.Activate();
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            var result = await navigation.NavigateProjectAsync(projectId);
            if (result == KastnProjectNavigationStatus.SaveBlocked)
                statusText.Text = "Save or resolve the current slip before opening the requested project.";
        }
    }

    internal void ReportActivationFailure(Exception exception)
    {
        if (lifetime.IsRetired) return;
        statusText.Text = $"Kastn could not open the requested project. {exception.Message}";
    }

    private void RefreshFilterChoices(ZetlProjectSnapshot project)
    {
        var selectedSource = (sourceFilterBox.SelectedItem as FilterItem)?.Value;
        var selectedSession = (sessionFilterBox.SelectedItem as FilterItem)?.Value;
        var distinctSources = project.Slips
            .Select(slip => slip.Source)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(source => source, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var distinctSessions = project.Slips
            .Select(slip => slip.SessionId)
            .Where(session => !string.IsNullOrWhiteSpace(session))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(session => session, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Rebuilding the bound combo lists item-by-item is visible churn; skip it
        // when the choices themselves have not changed (they only change when a
        // new source or session appears, not on ordinary edits).
        var signature = string.Join('', distinctSources)
            + ''
            + string.Join('', distinctSessions);
        if (string.Equals(signature, lastFilterChoicesSignature, StringComparison.Ordinal))
        {
            return;
        }

        lastFilterChoicesSignature = signature;
        sources.Clear();
        sources.Add(new FilterItem(null, "All sources"));
        foreach (var source in distinctSources)
        {
            sources.Add(new FilterItem(source, source));
        }

        sessions.Clear();
        sessions.Add(new FilterItem(null, "All sessions"));
        foreach (var session in distinctSessions)
        {
            sessions.Add(new FilterItem(session, ShortSession(session!)));
        }

        sourceFilterBox.SelectedItem = sources.FirstOrDefault(
                item => item.Value == selectedSource)
            ?? sources[0];
        sessionFilterBox.SelectedItem = sessions.FirstOrDefault(
                item => item.Value == selectedSession)
            ?? sessions[0];
    }

    private string lastFilterChoicesSignature = "";

    private void RefreshBuckets(ZetlProjectSnapshot project, string? selectedBucketId, bool refreshDetails = true)
    {
        // Capture the whole current selection (tree node ids) before the rebuild, so
        // an action that refreshes keeps the entire group selected — not just the
        // first node. Runs inside ApplySnapshot's refreshing guard, so re-selecting
        // drives no side effects here.
        var rememberedIds = projectTree.SelectedItems?.OfType<KastnTreeNode>()
            .Select(node => node.Id)
            .ToList() ?? [];

        // Reconcile the live projection, then update its expanded viewport.
        // Realized rows keep model identity; expansion, selection and the
        // first visible row survive changes to the logical hierarchy.
        var treeAnchor = projectTree.CaptureAnchor();
        if (treeProjection.Update(ProjectIndex, showingDeleted, this.settings.Current.MaxSlipLabelLength))
        {
            projectTree.SetHierarchy(treeProjection.Roots);
            projectTree.RestoreAnchor(treeAnchor);
        }

        UpdateDeletedToggle(project);

        // A pending override (after creating/moving a slip) wins; else restore the
        // remembered group; else fall back to the requested bucket / first slip /
        // first bucket so the editor and tree agree on open.
        var restoreIds = navigation.RestoreTreeSelection(rememberedIds, selectedBucketId,
            id => treeProjection.Find(id) is not null, treeProjection.FirstSlip?.Id,
            project.Buckets.FirstOrDefault()?.Id);

        ApplyTreeNodeSelection(restoreIds);
        if (refreshDetails)
        {
            RefreshBucketEditor();
            RefreshDestinationBuckets();
            SetDetailPaneMode(detailShowingMetadata);
        }
    }

    // Re-select a set of tree nodes by id: a single node sets SelectedItem, several
    // populate SelectedItems (multi). Runs under the refreshing guard.
    private void ApplyTreeNodeSelection(IReadOnlyList<string> ids)
    {
        var nodes = ids
            .Select(id => treeProjection.Find(id))
            .OfType<KastnTreeNode>()
            .ToList();
        // Resetting SelectedItems walks every realized row. Snapshot reconciliation
        // already updated these live nodes; preserve an unchanged selection.
        if (projectTree.SelectedItems is { } selected
            && selected.Cast<object>().SequenceEqual(nodes)
            && (nodes.Count != 0 || projectTree.SelectedItem is null))
            return;
        if (nodes.Count <= 1)
        {
            projectTree.SelectedItems?.Clear();
            projectTree.SelectedItem = nodes.FirstOrDefault();
            return;
        }

        projectTree.SelectedItems?.Clear();
        foreach (var node in nodes)
        {
            projectTree.SelectedItems?.Add(node);
        }
    }

    // The Deleted toggle shows the soft-delete count and stays available while the
    // Deleted view is open (so the user can exit even after restoring everything).
    private void UpdateDeletedToggle(ZetlProjectSnapshot project)
    {
        var deletedBucketIds = project.Buckets
            .Where(KastnWorkbench.IsDeletedBucket)
            .Select(bucket => bucket.Id)
            .ToHashSet(StringComparer.Ordinal);
        var deletedCount = project.Slips.Count(slip => deletedBucketIds.Contains(slip.BucketId));
        viewDeletedButton.Content = showingDeleted
            ? "Read Slips"
            : deletedCount > 0 ? $"Deleted ({deletedCount})" : "Deleted";
        viewDeletedButton.IsEnabled = deletedCount > 0 || showingDeleted;
        viewDeletedButton.IsChecked = showingDeleted;
    }

    private void OnViewDeletedToggled()
    {
        if (refreshing)
        {
            return;
        }

        showingDeleted = viewDeletedButton.IsChecked == true;
        // Re-apply the current snapshot so the tree rebuilds in the chosen mode and
        // the editor/inspector/View re-sync through the normal path.
        ApplySnapshot(connection.Current);
    }

    private void RefreshBucketEditor()
    {
        var selected = SelectedBucket;
        var isDeleted = KastnWorkbench.IsDeletedBucket(selected);
        bucketNameBox.Text = selected?.Name ?? "";
        bucketNameBox.IsEnabled = selected is not null && !isDeleted && IsOnline;
        saveBucketButton.IsEnabled = selected is not null && !isDeleted && IsOnline;
        deleteBucketButton.IsEnabled = selected is not null && !isDeleted && IsOnline;

        parentBuckets.Clear();
        parentBuckets.Add(new KastnBucketItem(null, "No parent (top level)", null));
        if (currentProject is { } project)
        {
            foreach (var item in KastnWorkbench.BuildBucketPickerChoices(ProjectIndex)
                .Where(item => selected is null
                    || (item.Id != selected.Id
                        && !KastnWorkbench.IsDeletedBucket(item.Bucket)
                        && !ProjectIndex.IsDescendant(item.Id!, selected.Id))))
            {
                parentBuckets.Add(item);
            }
        }

        parentBucketBox.SelectedItem = parentBuckets.FirstOrDefault(
                item => item.Id == selected?.ParentBucketId)
            ?? parentBuckets[0];
        parentBucketBox.IsEnabled = selected is not null && !isDeleted && IsOnline;

        var renderKind = selected?.RenderKind ?? "";
        bucketRenderKindBox.SelectedItem = bucketRenderKinds.FirstOrDefault(
            item => string.Equals(item.Value, renderKind, StringComparison.OrdinalIgnoreCase))
            ?? bucketRenderKinds[0];
        bucketRenderKindBox.IsEnabled = selected is not null && !isDeleted && IsOnline;

        RefreshParentBucketHint(selected);

        // The heading-style controls appear when a (non-deleted) bucket node is the
        // tree selection — that's where you style the bucket's title.
        var headingBucket = SelectedTreeNode is { Kind: KastnTreeNodeKind.Bucket, Bucket: { } hb }
            && !KastnWorkbench.IsDeletedBucket(hb)
            ? hb
            : null;
        bucketHeadingPanel.IsVisible = headingBucket is not null;
        if (headingBucket is not null)
        {
            PopulateBucketHeadingControls(headingBucket);
        }
    }

    private void PopulateBucketHeadingControls(ZetlBucketSnapshot bucket)
    {
        bucketHeadingUpdating = true;
        bucketHeadingSizeBox.SelectedIndex = bucket.HeadingLevel switch { 1 => 1, 3 => 2, _ => 0 };
        bucketHeadingAlignBox.SelectedIndex = bucket.HeadingAlign switch { "center" => 1, "right" => 2, _ => 0 };
        bucketHeadingBoldCheck.IsChecked = bucket.HeadingBold;
        bucketHeadingSizeBox.IsEnabled = IsOnline;
        bucketHeadingAlignBox.IsEnabled = IsOnline;
        bucketHeadingBoldCheck.IsEnabled = IsOnline;
        bucketHeadingUpdating = false;
    }

    private async Task OnBucketHeadingChangedAsync()
    {
        if (bucketHeadingUpdating
            || SelectedTreeNode is not { Kind: KastnTreeNodeKind.Bucket, Bucket: { } bucket }
            || KastnWorkbench.IsDeletedBucket(bucket))
        {
            return;
        }

        var align = bucketHeadingAlignBox.SelectedIndex switch { 1 => "center", 2 => "right", _ => "" };
        var level = bucketHeadingSizeBox.SelectedIndex switch { 1 => 1, 2 => 3, _ => 2 };
        await SendBucketHeadingAsync(bucket, align, bucketHeadingBoldCheck.IsChecked == true, level);
    }

    private async Task SendBucketHeadingAsync(ZetlBucketSnapshot bucket, string align, bool bold, int level)
    {
        if (!IsOnline || saving || currentProject is null) return;
        var current = CaptureNativeAction();
        await RunNativeCommandAsync(ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"),
            ZetlCommandKind.SetBucketHeading, new SetBucketHeadingCommand { Align = align, Bold = bold, Level = level },
            currentProject.Id, bucket.Id, bucket.Revision), "Bucket heading saved.",
            () => current() && CurrentBucketTarget(bucket));
    }

    private void RefreshDestinationBuckets()
    {
        var selectedSlips = SelectedSlips();
        var selectedSlip = selectedSlips.Count == 1 ? selectedSlips[0] : null;
        var selectedSlipIsDeleted = selectedSlip is not null && IsSlipInDeleted(selectedSlip);
        moveBuckets.Clear();
        if (currentProject is { } project)
        {
            foreach (var item in KastnWorkbench.BuildBucketPickerChoices(ProjectIndex)
                .Where(item => (selectedSlips.Count != 1 || item.Id != selectedSlip?.BucketId)
                    && !KastnWorkbench.IsDeletedBucket(item.Bucket)))
            {
                moveBuckets.Add(item);
            }
        }

        moveBucketBox.SelectedItem = selectedSlipIsDeleted
            ? moveBuckets.FirstOrDefault(item => item.Id == selectedSlip?.DeletedFromBucketId)
                ?? moveBuckets.FirstOrDefault(item => string.Equals(
                    item.Bucket?.Name,
                    "Scratch",
                    StringComparison.OrdinalIgnoreCase))
                ?? moveBuckets.FirstOrDefault()
            : moveBuckets.FirstOrDefault();
        SetEditingEnabled();
    }

    private void RefreshSlipView(bool force = false)
    {
        if (lifetime.IsRetired || (!force && refreshing) || currentProject is null)
        {
            return;
        }

        var wasRefreshing = refreshing;
        refreshing = true;
        try
        {
            BindSlipEditor();
            RefreshViewer();
            RefreshDestinationBuckets();
        }
        finally { refreshing = wasRefreshing; }
    }

    private void BindSlipEditor()
    {
        UpdateFilterButton();
        var binding = navigation.ResolveEditor(CurrentFilteredSlips(), CurrentSelection());
        if (!binding.Bind) return;
        editorState.Select(binding.Slip);
        UpdateEditorFromState();
        if (binding.Focus && binding.Slip is { } selected)
        {
            slipEditor.Focus();
            if (IsUntitledKastnSlip(selected)) slipEditor.SelectAll();
            else slipEditor.CaretIndex = slipEditor.Text?.Length ?? 0;
        }
    }

    private void UpdateFilterButton()
    {
        var activeCount = 0;
        if ((sourceFilterBox.SelectedItem as FilterItem)?.Value is not null)
        {
            activeCount++;
        }

        if ((sessionFilterBox.SelectedItem as FilterItem)?.Value is not null)
        {
            activeCount++;
        }

        if (dateFilterBox.SelectedItem is DateFilterItem date
            && date.Value != KastnDateFilter.All)
        {
            activeCount++;
        }

        if ((typeFilterBox.SelectedItem as TypeFilterItem)?.Value is not null)
        {
            activeCount++;
        }

        filtersButton.Content = activeCount == 0 ? "Filters ▾" : $"Filters ({activeCount}) ▾";
    }

    private async Task CloseProjectAsync()
    {
        if (currentProject is null) return;
        var result = await navigation.NavigateProjectAsync(null, beforeNavigate: () =>
        {
            editorState.Select(null);
            UpdateEditorFromState();
        });
        if (result == KastnProjectNavigationStatus.SaveBlocked)
            statusText.Text = "Save or resolve the current slip before closing the project.";
    }

    private void SetConnectionState(KastnSessionSnapshot snapshot)
    {
        var creationStatus = MutationStatus(snapshot) ?? CreationStatus(snapshot);
        statusText.Text = recoveredDraftActive
            ? editorState.ConflictCurrent is not null
                ? "Recovered local draft conflicts with the current Zetl slip."
                : "Recovered unsaved local draft; not yet saved to Zetl."
            : editorState.IsDirty
                ? "Unsaved changes."
                : creationStatus ?? snapshot.Status;
        var online = snapshot.ConnectionState == KastnConnectionState.Online;
        offlineBanner.IsVisible = snapshot.ConnectionState == KastnConnectionState.Offline;
        connectionProgress.IsVisible =
            snapshot.ConnectionState == KastnConnectionState.Connecting;
        connectionIndicator.Fill = online
            ? new SolidColorBrush(Color.Parse("#71D49B"))
            : new SolidColorBrush(Color.Parse("#FFB86B"));
        refreshMenuItem.IsEnabled = online;
        closeProjectMenuItem.IsEnabled = currentProject is not null;
        deleteProjectMenuItem.IsEnabled = online && currentProject is not null;
        journalModeMenuItem.IsEnabled = online && currentProject is not null;
        journalModeMenuItem.IsChecked = currentProject?.JournalMode == true;
        focusProjectsMenuItem.IsEnabled = landingProjectsPanel.IsVisible;
        newSlipMenuItem.IsEnabled = online
            && currentProject is not null
            && !KastnWorkbench.IsDeletedBucket(SelectedBucket);
        SetEditingEnabled();
        RefreshBucketEditor();
    }

    private async Task RefreshAsync()
    {
        if (lifetime.IsRetired || lifetime.AllowClose) return;
        var current = CaptureNativeAction(trackEditor: false);
        try
        {
            await SaveEditorAsync();
            if (!current()) return;
            await connection.RefreshAsync();
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (current() && !editorState.IsDirty) statusText.Text = ex.Message;
        }
    }

    private void HandleSimpleResponse(ZetlResponseEnvelope response, string success)
    {
        if (response.Status == ZetlResponseStatus.Conflict
            && response.Conflict?.TargetKind == ZetlEntityKind.Slip)
        {
            if (editorState.ReconcileConflict(response))
            {
                UpdateEditorFromState();
            }

            statusText.Text = editorState.ConflictCurrent is null
                ? "The slip changed in Zetl. Review it and try again."
                : "Resolve the slip conflict before continuing.";
            return;
        }

        if (response.Status == ZetlResponseStatus.Conflict
            && response.Conflict?.TargetKind == ZetlEntityKind.Project)
        {
            statusText.Text = "The project changed in Zetl. Refresh and try again.";
            return;
        }

        statusText.Text = response.Status == ZetlResponseStatus.Success
            ? success
            : response.Error?.Message ?? $"Operation failed: {response.Status}.";
    }

    private void ResetSlipFilters()
    {
        searchBox.Text = "";
        sourceFilterBox.SelectedItem = sources.FirstOrDefault();
        sessionFilterBox.SelectedItem = sessions.FirstOrDefault();
        dateFilterBox.SelectedItem = dates.FirstOrDefault();
        typeFilterBox.SelectedIndex = 0;
    }

    private async void OnSlipEditorPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || lifetime.IsRetired || lifetime.AllowClose) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.Enter or Key.S)
        {
            e.Handled = true;
            await SaveEditorAsync();
        }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.Z or Key.Y)
        {
            // One ordered history for everything: Ctrl+Z/Ctrl+Y in the editor drive
            // Kastn's undo the same as anywhere else (the box's own undo is
            // disabled). Tunnel phase, so this claims the key before the TextBox.
            e.Handled = true;
            await (e.Key == Key.Z ? UndoLastAsync() : RedoLastAsync());
        }
        else if (e.Key == Key.F12 && currentProject is not null)
        {
            var text = slipEditor.Text ?? "";
            var caret = slipEditor.CaretIndex;
            var link = ZetlSlipLinks.FindAt(text, caret);
            if (link is not null && currentProject.Slips.Any(slip => slip.Id == link.TargetId))
            {
                e.Handled = true;
                ReselectSlipNode(link.TargetId);
            }
        }
    }

    private void OnSlipEditorPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && currentProject is not null)
        {
            var text = slipEditor.Text ?? "";
            var caret = slipEditor.CaretIndex;
            var link = ZetlSlipLinks.FindAt(text, caret);
            if (link is not null && currentProject.Slips.Any(slip => slip.Id == link.TargetId))
            {
                ReselectSlipNode(link.TargetId);
                e.Handled = true;
            }
        }

        UpdateInlineFormatButtons();
    }

    private void OnSlipEditorKeyUp(object? sender, KeyEventArgs e)
    {
        UpdateInlineFormatButtons();
    }

    private async void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Handled || lifetime.IsRetired || lifetime.AllowClose) return;
        if (args.Key == Key.F5)
        {
            args.Handled = true;
            await RefreshAsync();
        }
        else if (args.KeyModifiers.HasFlag(KeyModifiers.Control) && args.Key == Key.F)
        {
            args.Handled = true;
            FocusSearch();
        }
        else if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.N)
        {
            if (!newSlipMenuItem.IsEnabled) return;
            args.Handled = true;
            await AddSlipAsync();
        }
        else if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.B)
        {
            args.Handled = true;
            SetBoardMode(!boardModeActive);
        }
        else if (args.KeyModifiers.HasFlag(KeyModifiers.Control)
            && args.Key is Key.S or Key.Enter)
        {
            // Ctrl+S or Ctrl+Enter saves the current slip (Enter alone inserts a
            // newline in the editor). The editor's own preview handler catches these
            // when it is focused; this covers a save from elsewhere.
            args.Handled = true;
            await SaveEditorAsync();
        }
        else if (args.KeyModifiers.HasFlag(KeyModifiers.Control) && args.Key == Key.W)
        {
            args.Handled = true;
            await CloseProjectAsync();
        }
        else if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.Z)
        {
            // In-app undo. An incidental text box (bucket name, search) keeps its
            // own Ctrl+Z; the slip editor's tunnel handler already claimed the key
            // for Kastn's history before this ever runs. Held Ctrl+Z remains a
            // Zetl hold shortcut and never reaches Kastn.
            if (IsTextInputFocused())
            {
                return;
            }

            args.Handled = true;
            await UndoLastAsync();
        }
        else if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.Y)
        {
            // In-app redo. Ctrl+Y only — Ctrl+Shift+Z is deliberately avoided because
            // held Ctrl+Shift+Z is Zetl's Shift-lane undo hold shortcut, and mapping its tap
            // to redo would overload one chord with opposite meanings.
            if (IsTextInputFocused())
            {
                return;
            }

            args.Handled = true;
            await RedoLastAsync();
        }
        else if (args.Key == Key.F2 && SelectedBucket is not null)
        {
            args.Handled = true;
            bucketNameBox.Focus();
            bucketNameBox.SelectAll();
        }
    }

    private async void ShowAbout(object? sender, RoutedEventArgs args)
    {
        await KastnDialogs.MessageAsync(
            this,
            "Kastn",
            "A dwell workspace for browsing and organizing Zetl projects. "
                + "Zetl remains the sole writer.");
    }

    private void BringToForeground()
    {
        if (!OperatingSystem.IsWindows()
            || TryGetPlatformHandle() is not { Handle: { } handle }
            || handle == IntPtr.Zero)
        {
            return;
        }

        SetForegroundWindow(handle);
    }

    private bool IsOnline =>
        !lifetime.IsRetired && connection.Current.ConnectionState == KastnConnectionState.Online;

    private KastnTreeNode? SelectedTreeNode => projectTree.SelectedItem as KastnTreeNode;

    private string? SelectedBucketId => SelectedBucket?.Id;

    // The bucket a tree selection acts on: the bucket node itself, or the parent
    // bucket of a selected slip (so bucket add/move/rename target something sane
    // whether a bucket or a slip is selected).
    private ZetlBucketSnapshot? SelectedBucket
    {
        get
        {
            var node = SelectedTreeNode;
            if (node is null || currentProject is null)
            {
                return null;
            }

            return node.Kind == KastnTreeNodeKind.Bucket
                ? node.Bucket
                : ProjectIndex.Bucket(node.Slip!.BucketId);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    private sealed record FilterItem(string? Value, string Label);
    private sealed record TypeFilterItem(ZetlSlipType? Value, string Label);
    private sealed record DateFilterItem(KastnDateFilter Value, string Label);

    private void SetBoardMode(bool active)
    {
        if (lifetime.IsRetired || lifetime.AllowClose || boardModeActive == active) return;
        boardModeActive = active;
        boardModeMenuItem.IsChecked = active;

        viewerPanel.IsVisible = !active;
        boardPanel.IsVisible = active;

        if (active)
        {
            if (viewModeListButton.Classes.Contains("view-format-active"))
            {
                viewModeListButton.Classes.Remove("view-format-active");
            }
            if (!viewModeBoardButton.Classes.Contains("view-format-active"))
            {
                viewModeBoardButton.Classes.Add("view-format-active");
            }

            // Save current widths
            treeColumnWidth = mainColumnsGrid.ColumnDefinitions[0].Width;
            leftSplitterWidth = mainColumnsGrid.ColumnDefinitions[1].Width;
            rightSplitterWidth = mainColumnsGrid.ColumnDefinitions[3].Width;
            rightColumnWidth = mainColumnsGrid.ColumnDefinitions[4].Width;

            // Hide pane borders and splitters
            treePaneBorder.IsVisible = false;
            leftSplitter.IsVisible = false;
            rightPaneBorder.IsVisible = false;
            rightSplitter.IsVisible = false;

            // Collapse columns
            mainColumnsGrid.ColumnDefinitions[0].Width = new GridLength(0, GridUnitType.Pixel);
            mainColumnsGrid.ColumnDefinitions[1].Width = new GridLength(0, GridUnitType.Pixel);
            mainColumnsGrid.ColumnDefinitions[3].Width = new GridLength(0, GridUnitType.Pixel);
            mainColumnsGrid.ColumnDefinitions[4].Width = new GridLength(0, GridUnitType.Pixel);
        }
        else
        {
            if (!viewModeListButton.Classes.Contains("view-format-active"))
            {
                viewModeListButton.Classes.Add("view-format-active");
            }
            if (viewModeBoardButton.Classes.Contains("view-format-active"))
            {
                viewModeBoardButton.Classes.Remove("view-format-active");
            }

            // Show pane borders and splitters
            treePaneBorder.IsVisible = true;
            leftSplitter.IsVisible = true;
            rightPaneBorder.IsVisible = true;
            rightSplitter.IsVisible = true;

            // Restore columns
            mainColumnsGrid.ColumnDefinitions[0].Width = treeColumnWidth;
            mainColumnsGrid.ColumnDefinitions[1].Width = leftSplitterWidth;
            mainColumnsGrid.ColumnDefinitions[3].Width = rightSplitterWidth;
            mainColumnsGrid.ColumnDefinitions[4].Width = rightColumnWidth;
        }

        RefreshViewer();
    }
}

internal sealed record KastnRenderKindItem(string Value, string Label)
{
    public override string ToString() => Label;
}

internal sealed record KastnFontFamilyItem(string Value, string Label)
{
    public override string ToString() => Label;
}

internal sealed record KastnFontSizeItem(int Value, string Label)
{
    public override string ToString() => Label;
}

internal sealed record KastnTextColorItem(string Value, string Label, IBrush Swatch)
{
    public override string ToString() => Label;
}
