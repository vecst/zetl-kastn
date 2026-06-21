using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow : Window
{
    private const string UntitledSlipText = "Untitled";
    private const double LandingCardWidth = 450;
    private const double LandingCardHeight = 330;
    private const double LandingCardMargin = 8;
    private const int LandingMaxColumns = 6;
    private const int LandingMaxRows = 5;

    private readonly KastnConnectionController connection;
    // Built-ins plus user templates; corrupt/invalid user files are skipped with a
    // diagnostic written to Console.Error (Kastn's existing diagnostic channel).
    private readonly KastnTemplateCatalog templateCatalog = new(Console.Error.WriteLine);
    private IReadOnlyList<ZetlTemplateDocument> loadedTemplates = [];
    // Built-in plus user views for the read-view renderer.
    private readonly ZetlViewStore viewStore = new(log: Console.Error.WriteLine);
    private IReadOnlyList<ZetlViewDocument> loadedViews = ZetlViewDefaults.CreateAll();
    private string lastRenderedViewText = "";
    private static readonly string[] ViewKindChoices =
    [
        ZetlViewKinds.Formatted,
        ZetlViewKinds.Plain,
        ZetlViewKinds.Tsv,
        ZetlViewKinds.Markdown,
        ZetlViewKinds.Html,
        ZetlViewKinds.Pdf
    ];
    private ZetlViewDocument? editingView;
    private bool viewEditorUpdating;
    private string viewBaselineJson = "";
    // Creation types: bundle a template with a default view.
    private readonly ZetlCreationTypeStore creationStore = new(log: Console.Error.WriteLine);
    private readonly ObservableCollection<CreationListItem> creations = [];
    private bool landingShowingCreations;
    private ZetlCreationTypeDocument? editingCreation;
    private string creationBaselineJson = "";
    // Sentinel for the creation editor's "no view" choice.
    private static readonly ZetlViewDocument NoView = new() { Id = "", Name = "(no view)" };
    // The in-window template editor's working state. Non-null while the editor view
    // is active; the document is a clone/draft, so cancelling discards changes.
    private readonly ObservableCollection<TemplateBucketItem> templateBuckets = [];
    private static readonly string[] TemplateTypeChoices =
        [ZetlTemplateTypes.Capture, ZetlTemplateTypes.Consumable];
    private static readonly string[] TemplateKindChoices = ["Standard", "Replay"];
    private static readonly string[] TemplateCompileChoices = ["Formatted", "Plain", "TSV"];
    private ZetlTemplateDocument? editingTemplate;
    private bool editingTemplateIsNew;
    private ZetlTemplateBucketDocument? selectedTemplateBucket;
    private bool templateEditorUpdating;
    // Serialized working document at open, to detect unsaved edits on cancel.
    private string templateBaselineJson = "";
    private readonly ObservableCollection<ProjectListItem> projects = [];
    private readonly ObservableCollection<TemplateListItem> templates = [];
    private readonly ObservableCollection<KastnBucketItem> buckets = [];
    private readonly ObservableCollection<SlipListItem> slips = [];
    private readonly ObservableCollection<FilterItem> sources = [];
    private readonly ObservableCollection<FilterItem> sessions = [];
    private readonly ObservableCollection<DateFilterItem> dates = [];
    private readonly ObservableCollection<KastnBucketItem> parentBuckets = [];
    private readonly ObservableCollection<KastnBucketItem> moveBuckets = [];
    private readonly KastnEditorState editorState = new();
    private readonly List<EditableSlipBlock> editableViewBlocks = [];
    private readonly DispatcherTimer autosaveTimer;
    private ZetlProjectSnapshot? currentProject;
    private bool refreshing;
    private bool editorUpdating;
    private bool saving;
    private bool savingEditableView;
    private string? pendingSaveText;
    private string? pendingBucketSelectionId;
    private string? pendingSlipSelectionId;
    private string? pendingEditableBlockFocusId;
    private bool pendingSlipFocus;
    private bool viewerMode = true;
    private bool editableViewMode;
    private bool landingShowingTemplates;
    private bool landingShowingConsumable;
    private bool suppressLandingProjectSelection;

    public MainWindow()
    {
        InitializeComponent();
        connection = null!;
        autosaveTimer = null!;
    }

    public MainWindow(KastnConnectionController connection)
    {
        this.connection = connection;
        InitializeComponent();
        landingProjectList.ItemsSource = projects;
        landingTemplateItems.ItemsSource = templates;
        bucketList.ItemsSource = buckets;
        slipList.ItemsSource = slips;
        sourceFilterBox.ItemsSource = sources;
        sessionFilterBox.ItemsSource = sessions;
        dateFilterBox.ItemsSource = dates;
        parentBucketBox.ItemsSource = parentBuckets;
        moveBucketBox.ItemsSource = moveBuckets;
        editViewMoveBucketBox.ItemsSource = moveBuckets;

        dates.Add(new DateFilterItem(KastnDateFilter.All, "All time"));
        dates.Add(new DateFilterItem(KastnDateFilter.Today, "Today"));
        dates.Add(new DateFilterItem(KastnDateFilter.Last7Days, "Last 7 days"));
        dates.Add(new DateFilterItem(KastnDateFilter.Last30Days, "Last 30 days"));
        dateFilterBox.SelectedIndex = 0;
        RebuildTemplateCards();

        autosaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        autosaveTimer.Tick += async (_, _) =>
        {
            autosaveTimer.Stop();
            await SaveEditorAsync();
        };

        connection.SnapshotChanged += OnSnapshotChanged;
        landingProjectList.SelectionChanged += OnProjectSelectionChanged;
        bucketList.SelectionChanged += (_, _) =>
        {
            if (!refreshing)
            {
                RefreshBucketEditor();
                RefreshSlipView(force: true);
            }
        };
        slipList.SelectionChanged += OnSlipSelectionChanged;
        searchBox.TextChanged += (_, _) => RefreshSlipView();
        sourceFilterBox.SelectionChanged += (_, _) => RefreshSlipView();
        sessionFilterBox.SelectionChanged += (_, _) => RefreshSlipView();
        dateFilterBox.SelectionChanged += (_, _) => RefreshSlipView();
        slipEditor.TextChanged += (_, _) => OnEditorTextChanged();

        refreshMenuItem.Click += async (_, _) => await RefreshAsync();
        closeProjectMenuItem.Click += async (_, _) => await CloseProjectAsync();
        deleteProjectMenuItem.Click += async (_, _) => await DeleteProjectAsync();
        exitMenuItem.Click += (_, _) => Close();
        newSlipMenuItem.Click += async (_, _) => await AddSlipAsync();
        saveSlipMenuItem.Click += async (_, _) => await SaveEditorAsync();
        deleteSlipMenuItem.Click += async (_, _) => await DeleteSlipAsync();
        viewerModeMenuItem.Click += async (_, _) => await ToggleEditableViewAsync();
        focusSearchMenuItem.Click += (_, _) => searchBox.Focus();
        focusProjectsMenuItem.Click += (_, _) => landingProjectList.Focus();
        focusBucketsMenuItem.Click += (_, _) => bucketList.Focus();
        focusSlipsMenuItem.Click += (_, _) => FocusMainView();
        aboutMenuItem.Click += ShowAbout;
        addBucketButton.Click += async (_, _) => await AddBucketAsync();
        saveBucketButton.Click += async (_, _) => await SaveBucketAsync();
        deleteBucketButton.Click += async (_, _) => await DeleteBucketAsync();
        closeProjectButton.Click += async (_, _) => await CloseProjectAsync();
        editViewButton.Click += async (_, _) => await ToggleEditableViewAsync();
        saveViewButton.Click += async (_, _) => await SaveEditableViewAsync();
        cancelViewButton.Click += (_, _) => CancelEditableView();
        moveSelectedViewButton.Click += async (_, _) => await MoveSelectedEditableBlocksAsync();
        newSlipButton.Click += async (_, _) => await AddSlipAsync();
        loadedViews = viewStore.LoadAll();
        viewPickerBox.ItemsSource = loadedViews;
        viewPickerBox.SelectedIndex = 0;
        viewPickerBox.SelectionChanged += (_, _) =>
        {
            if (!refreshing)
            {
                RefreshViewer();
            }
        };
        copyViewButton.Click += async (_, _) => await CopyRenderedViewAsync();
        exportViewButton.Click += async (_, _) => await ExportRenderedViewAsync();
        viewKindBox.ItemsSource = ViewKindChoices;
        saveViewSettingsButton.Click += (_, _) => SaveView();
        cancelViewSettingsButton.Click += async (_, _) => await CancelViewEditAsync();
        viewKindBox.SelectionChanged += (_, _) =>
        {
            if (!viewEditorUpdating)
            {
                ApplyViewKindTsvVisibility();
            }
        };
        newViewMenuItem.Click += (_, _) => OpenViewEditor(
            new ZetlViewDocument { Name = "", Category = "Custom", Kind = ZetlViewKinds.Markdown },
            isNew: true);
        editViewSettingsMenuItem.Click += (_, _) => EditSelectedView();
        deleteViewMenuItem.Click += async (_, _) => await DeleteSelectedViewAsync();
        saveSlipButton.Click += async (_, _) => await SaveEditorAsync();
        deleteSlipButton.Click += async (_, _) => await DeleteSlipAsync();
        moveSlipButton.Click += async (_, _) => await MoveSlipAsync();
        restoreSlipButton.Click += async (_, _) => await RestoreSlipAsync();
        useZetlButton.Click += (_, _) => UseZetlVersion();
        keepMineButton.Click += async (_, _) => await KeepMineAsync();
        landingProjectsButton.Click += (_, _) => ShowLandingSection(templates: false, creations: false);
        landingTemplatesButton.Click += (_, _) => ShowLandingSection(templates: true, creations: false);
        landingCreateButton.Click += (_, _) => ShowLandingSection(templates: false, creations: true);
        landingCaptureButton.Click += (_, _) => SetTemplateType(consumable: false);
        landingConsumableButton.Click += (_, _) => SetTemplateType(consumable: true);
        landingCreationItems.ItemsSource = creations;
        WireTemplateEditor();
        WireCreationEditor();
        SizeChanged += (_, _) => RefreshLandingGridLayout();
        KeyDown += OnKeyDown;
        Closed += (_, _) =>
        {
            autosaveTimer.Stop();
            connection.SnapshotChanged -= OnSnapshotChanged;
        };
        ApplySnapshot(connection.Current);
    }

    public async void ActivateRequest(string? projectId)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        BringToForeground();
        await connection.NavigateToProjectAsync(projectId);
    }

    private void OnSnapshotChanged(object? sender, KastnSessionSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(KastnSessionSnapshot snapshot)
    {
        var priorProjectId = currentProject?.Id;
        var selectedProjectId = snapshot.Project?.Id;
        var selectedBucketId = SelectedBucketId;
        var selectedSlipId = pendingSlipSelectionId ?? editorState.SlipId;

        refreshing = true;
        try
        {
            projects.Clear();
            foreach (var project in snapshot.Projects)
            {
                projects.Add(new ProjectListItem(
                    project.Id,
                    project.Name,
                    project.MetadataRevision,
                    LandingProjectDetail(project),
                    string.IsNullOrWhiteSpace(project.PreviewText)
                        ? "No notes yet"
                        : project.PreviewText,
                    LandingProjectActivity(project)));
            }
            RefreshLandingGridLayout();

            landingProjectList.SelectedItem = null;

            currentProject = snapshot.Project;
            if (currentProject is { } projectSnapshot)
            {
                if (!string.Equals(priorProjectId, projectSnapshot.Id, StringComparison.Ordinal))
                {
                    selectedBucketId = null;
                    selectedSlipId = null;
                    editorState.Select(null);
                    searchBox.Text = "";
                    // On opening a project, render with its default view (set by a
                    // creation type, or chosen earlier), falling back to the first.
                    SelectViewForProject(projectSnapshot);
                }

                projectTitle.Text = projectSnapshot.Name;
                projectSummary.Text =
                    $"{projectSnapshot.Buckets.Count} buckets, "
                    + $"{projectSnapshot.Slips.Count} slips, "
                    + $"change {projectSnapshot.ChangeSequence}";
                RefreshFilterChoices(projectSnapshot);
                RefreshBuckets(projectSnapshot, pendingBucketSelectionId ?? selectedBucketId);
                pendingBucketSelectionId = null;

                var currentSlip = selectedSlipId is null
                    ? null
                    : projectSnapshot.Slips.FirstOrDefault(slip => slip.Id == selectedSlipId);
                editorState.Reconcile(currentSlip, pendingSaveText);
                UpdateEditorFromState();
                RefreshSlipView(force: true);
                projectView.IsVisible = true;
                emptyState.IsVisible = false;
            }
            else
            {
                currentProject = null;
                buckets.Clear();
                slips.Clear();
                editorState.Select(null);
                UpdateEditorFromState();
                RefreshViewer();
                projectView.IsVisible = false;
                emptyState.IsVisible = true;
                var showLandingChoices = snapshot.ConnectionState == KastnConnectionState.Online
                    && (snapshot.Projects.Count > 0 || KastnTemplateCatalog.BuiltIns.Count > 0);
                landingModeToggle.IsVisible = showLandingChoices;
                emptyStateText.Text = snapshot.ConnectionState == KastnConnectionState.Online
                    ? "Select a project or template to begin."
                    : snapshot.Status;
                RefreshLandingMode();
            }

            // The in-window template/view editors are modal-in-spirit: once open they
            // stay up across live snapshots (they edit local files, not the project),
            // so keep them on top of whatever the project/landing logic just decided.
            if (editingTemplate is not null)
            {
                projectView.IsVisible = false;
                emptyState.IsVisible = false;
                viewEditorView.IsVisible = false;
                templateEditorView.IsVisible = true;
            }
            else if (editingView is not null)
            {
                projectView.IsVisible = false;
                emptyState.IsVisible = false;
                templateEditorView.IsVisible = false;
                viewEditorView.IsVisible = true;
            }
            else if (editingCreation is not null)
            {
                projectView.IsVisible = false;
                emptyState.IsVisible = false;
                templateEditorView.IsVisible = false;
                viewEditorView.IsVisible = false;
                creationEditorView.IsVisible = true;
            }

            SetConnectionState(snapshot);
        }
        finally
        {
            refreshing = false;
        }
    }

    private void ShowLandingSection(bool templates, bool creations)
    {
        landingShowingTemplates = templates;
        landingShowingCreations = creations;
        landingProjectList.SelectedItem = null;
        // Re-read each catalog from disk on visit so added/removed user files show
        // up without restarting Kastn.
        if (templates)
        {
            RebuildTemplateCards();
        }

        if (creations)
        {
            RebuildCreationCards();
        }

        RefreshLandingMode();
    }

    private void SetTemplateType(bool consumable)
    {
        landingShowingConsumable = consumable;
        RebuildTemplateCards();
        RefreshLandingMode();
    }

    private void RebuildTemplateCards()
    {
        var type = landingShowingConsumable
            ? ZetlTemplateTypes.Consumable
            : ZetlTemplateTypes.Capture;
        loadedTemplates = templateCatalog.LoadAll();
        templates.Clear();
        foreach (var template in loadedTemplates.Where(
            item => string.Equals(item.Type, type, StringComparison.Ordinal)))
        {
            templates.Add(new TemplateListItem(
                template.Category,
                template.Name,
                template.Description,
                template,
                !ZetlTemplateDefaults.IsBuiltIn(template.Id)));
        }

        RefreshLandingGridLayout();
    }

    private void RefreshLandingMode()
    {
        var showChoices = emptyState.IsVisible && landingModeToggle.IsVisible;
        var showProjects = !landingShowingTemplates && !landingShowingCreations;
        landingProjectList.IsVisible = showChoices && showProjects && projects.Count > 0;
        landingTemplateList.IsVisible = showChoices && landingShowingTemplates;
        landingTemplateList.IsEnabled = IsOnline;
        landingCreationList.IsVisible = showChoices && landingShowingCreations;
        landingCreationList.IsEnabled = IsOnline;
        landingTemplateTypeToggle.IsVisible = showChoices && landingShowingTemplates;
        landingTemplateTypeToggle.IsEnabled = IsOnline;
        landingNewTemplateButton.IsVisible = showChoices && landingShowingTemplates;
        landingNewCreationButton.IsVisible = showChoices && landingShowingCreations;
        landingProjectsButton.IsEnabled = !showProjects;
        landingTemplatesButton.IsEnabled = !landingShowingTemplates;
        landingCreateButton.IsEnabled = !landingShowingCreations;
        landingCaptureButton.IsEnabled = landingShowingConsumable;
        landingConsumableButton.IsEnabled = !landingShowingConsumable;
        RefreshLandingGridLayout();
    }

    private void RefreshLandingGridLayout()
    {
        var cardOuterWidth = LandingCardWidth + (LandingCardMargin * 2);
        var cardOuterHeight = LandingCardHeight + (LandingCardMargin * 2);
        var cardCount = Math.Max(1, landingShowingCreations
            ? creations.Count
            : landingShowingTemplates ? templates.Count : projects.Count);

        var availableWidth = Math.Max(cardOuterWidth, Bounds.Width - 120);
        var columnsByWidth = Math.Clamp((int)Math.Floor(availableWidth / cardOuterWidth), 1, LandingMaxColumns);
        var columns = Math.Min(cardCount, columnsByWidth);

        var availableHeight = Math.Max(cardOuterHeight, Bounds.Height - 190);
        var rowsByHeight = Math.Clamp((int)Math.Floor(availableHeight / cardOuterHeight), 1, LandingMaxRows);
        var neededRows = (int)Math.Ceiling(cardCount / (double)columns);
        var rows = Math.Min(rowsByHeight, Math.Min(LandingMaxRows, neededRows));

        landingProjectList.Width = (columns * cardOuterWidth) + 4;
        landingProjectList.MaxHeight = (rows * cardOuterHeight) + 4;
        landingTemplateList.Width = landingProjectList.Width;
        landingTemplateList.MaxHeight = landingProjectList.MaxHeight;
        landingCreationList.Width = landingProjectList.Width;
        landingCreationList.MaxHeight = landingProjectList.MaxHeight;
    }

    private static string LandingProjectDetail(ZetlProjectSummary project)
    {
        var detail = $"{project.VisibleSlipCount} slip{Plural(project.VisibleSlipCount)}"
            + $" | {project.VisibleBucketCount} bucket{Plural(project.VisibleBucketCount)}";
        return project.DeletedSlipCount == 0
            ? detail
            : $"{detail} | {project.DeletedSlipCount} deleted";
    }

    private static string LandingProjectActivity(ZetlProjectSummary project)
    {
        return project.LastActivityUtc is null
            ? "No activity yet"
            : $"Last note {project.LastActivityUtc.Value.LocalDateTime:g}";
    }

    private void RefreshFilterChoices(ZetlProjectSnapshot project)
    {
        var selectedSource = (sourceFilterBox.SelectedItem as FilterItem)?.Value;
        var selectedSession = (sessionFilterBox.SelectedItem as FilterItem)?.Value;
        sources.Clear();
        sources.Add(new FilterItem(null, "All sources"));
        foreach (var source in project.Slips
            .Select(slip => slip.Source)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(source => source, StringComparer.OrdinalIgnoreCase))
        {
            sources.Add(new FilterItem(source, source));
        }

        sessions.Clear();
        sessions.Add(new FilterItem(null, "All sessions"));
        foreach (var session in project.Slips
            .Select(slip => slip.SessionId)
            .Where(session => !string.IsNullOrWhiteSpace(session))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(session => session, StringComparer.OrdinalIgnoreCase))
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

    private void RefreshBuckets(ZetlProjectSnapshot project, string? selectedBucketId)
    {
        buckets.Clear();
        foreach (var bucket in KastnWorkbench.BuildBucketHierarchy(project, includeAll: true))
        {
            buckets.Add(bucket);
        }

        bucketList.SelectedItem = buckets.FirstOrDefault(
                bucket => bucket.Id == selectedBucketId)
            ?? buckets[0];
        RefreshBucketEditor();
        RefreshDestinationBuckets();
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
            foreach (var item in KastnWorkbench.BuildBucketPickerChoices(project)
                .Where(item => selected is null
                    || (item.Id != selected.Id
                        && !KastnWorkbench.IsDeletedBucket(item.Bucket)
                        && !IsDescendant(project, item.Id!, selected.Id))))
            {
                parentBuckets.Add(item);
            }
        }

        parentBucketBox.SelectedItem = parentBuckets.FirstOrDefault(
                item => item.Id == selected?.ParentBucketId)
            ?? parentBuckets[0];
        parentBucketBox.IsEnabled = selected is not null && !isDeleted && IsOnline;
        RefreshParentBucketHint(selected);
    }

    private void RefreshDestinationBuckets()
    {
        var selectedSlips = SelectedSlips();
        var selectedSlip = selectedSlips.Count == 1 ? selectedSlips[0] : null;
        var selectedSlipIsDeleted = selectedSlip is not null && IsSlipInDeleted(selectedSlip);
        moveBuckets.Clear();
        if (currentProject is { } project)
        {
            foreach (var item in KastnWorkbench.BuildBucketPickerChoices(project)
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
        editViewMoveBucketBox.SelectedItem = moveBuckets.FirstOrDefault(
                item => item.Id != SelectedBucketId)
            ?? moveBuckets.FirstOrDefault();
        SetEditingEnabled();
    }

    private void RefreshSlipView(bool force = false)
    {
        if ((!force && refreshing) || currentProject is null)
        {
            return;
        }

        var selectedId = pendingSlipSelectionId ?? editorState.SlipId;
        var selectedIds = SelectedSlipItems()
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var filtered = CurrentFilteredSlips();

        var wasRefreshing = refreshing;
        refreshing = true;
        try
        {
            slips.Clear();
            foreach (var slip in filtered)
            {
                var bucketName = currentProject.Buckets.FirstOrDefault(
                    bucket => bucket.Id == slip.BucketId)?.Name ?? "Unknown";
                slips.Add(new SlipListItem(
                    slip.Id,
                    SlipPreviewText(slip),
                    $"{bucketName} | {slip.Source} | {slip.CapturedAtUtc.LocalDateTime:g}",
                    slip));
            }

            var selected = slips.FirstOrDefault(item => item.Id == selectedId);
            if (pendingSlipSelectionId is null && selectedIds.Count > 1)
            {
                slipList.SelectedItems?.Clear();
                foreach (var item in slips.Where(item => selectedIds.Contains(item.Id)))
                {
                    slipList.SelectedItems?.Add(item);
                }

                editorState.Select(null);
                UpdateEditorFromState();
            }
            else if (selected is not null)
            {
                slipList.SelectedItem = selected;
                if (pendingSlipSelectionId == selected.Id)
                {
                    pendingSlipSelectionId = null;
                    editorState.Select(selected.Slip);
                    UpdateEditorFromState();
                    if (pendingSlipFocus)
                    {
                        pendingSlipFocus = false;
                        slipEditor.Focus();
                        if (IsUntitledKastnSlip(selected.Slip))
                        {
                            slipEditor.SelectAll();
                        }
                        else
                        {
                            slipEditor.CaretIndex = slipEditor.Text?.Length ?? 0;
                        }
                    }
                }
            }
            else if (!editorState.IsDirty && editorState.ConflictCurrent is null)
            {
                slipList.SelectedItem = slips.FirstOrDefault();
                editorState.Select((slipList.SelectedItem as SlipListItem)?.Slip);
                UpdateEditorFromState();
            }
            else
            {
                slipList.SelectedItem = null;
            }

            UpdateSlipCountText();
            RefreshViewer();
            RefreshDestinationBuckets();
        }
        finally
        {
            refreshing = wasRefreshing;
        }
    }

    private async void OnProjectSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (suppressLandingProjectSelection)
        {
            suppressLandingProjectSelection = false;
            refreshing = true;
            landingProjectList.SelectedItem = null;
            refreshing = false;
            return;
        }

        if (!refreshing
            && sender is ListBox listBox
            && listBox.SelectedItem is ProjectListItem project)
        {
            await OpenProjectCardAsync(project);
        }
    }

    private void OnProjectCardActionPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        suppressLandingProjectSelection = true;
        Dispatcher.UIThread.Post(
            () => suppressLandingProjectSelection = false,
            DispatcherPriority.Background);
    }

    private async void OnProjectCardOpenClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is ProjectListItem project)
        {
            await OpenProjectCardAsync(project);
        }
    }

    private async void OnProjectCardRenameClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is ProjectListItem project)
        {
            await RenameProjectAsync(project);
        }
    }

    private async void OnProjectCardDeleteClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is ProjectListItem project)
        {
            await DeleteProjectAsync(project);
        }
    }

    private async void OnUseTemplateClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is TemplateListItem template)
        {
            await CreateProjectFromTemplateAsync(template.Source);
        }
    }

    private void OnNewTemplateClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        OpenTemplateEditor(ZetlTemplateDefaults.CreateDraft(), isNew: true);
    }

    private void OnEditTemplateClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is TemplateListItem template)
        {
            // Edit a clone so cancelling leaves the saved file untouched, and so a
            // built-in (should one ever reach here) can never be mutated in place.
            OpenTemplateEditor(ZetlTemplateDefaults.Clone(template.Source), isNew: false);
        }
    }

    private void OnDuplicateTemplateClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is TemplateListItem template)
        {
            // A fresh id + name, so duplicating a built-in yields an editable copy
            // and the original preset stays immutable.
            OpenTemplateEditor(ZetlTemplateDefaults.Duplicate(template.Source), isNew: true);
        }
    }

    private async void OnDeleteTemplateClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is not TemplateListItem template)
        {
            return;
        }

        var confirmed = await KastnDialogs.ConfirmAsync(
            this,
            $"Delete the template '{template.Name}'? This cannot be undone.",
            "Delete");
        if (!confirmed)
        {
            return;
        }

        try
        {
            templateCatalog.Store.Delete(template.Source.Id);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            statusText.Text = $"Could not delete template: {ex.Message}";
            return;
        }

        RebuildTemplateCards();
        RefreshLandingMode();
        statusText.Text = $"Deleted template '{template.Name}'.";
    }

    // ---- In-window template editor (mirrors the project workbench) ----

    private void WireTemplateEditor()
    {
        templateBucketList.ItemsSource = templateBuckets;
        templateTypeBox.ItemsSource = TemplateTypeChoices;
        templateBucketKindBox.ItemsSource = TemplateKindChoices;
        templateBucketCompileBox.ItemsSource = TemplateCompileChoices;

        saveTemplateButton.Click += (_, _) => SaveTemplate();
        cancelTemplateButton.Click += async (_, _) => await CancelTemplateEditAsync();
        saveAsTemplateButton.Click += (_, _) => OpenTemplateFromProject();

        templateAddBucketButton.Click += (_, _) => AddTemplateBucket();
        templateDeleteBucketButton.Click += (_, _) => DeleteTemplateBucket();
        templateBucketUpButton.Click += (_, _) => MoveTemplateBucket(-1);
        templateBucketDownButton.Click += (_, _) => MoveTemplateBucket(1);
        templateBucketList.SelectionChanged += (_, _) => OnTemplateBucketSelected();

        templateTypeBox.SelectionChanged += (_, _) =>
        {
            if (!templateEditorUpdating)
            {
                ApplyTemplateSeedsVisibility();
            }
        };
        templateBucketNameBox.TextChanged += (_, _) => CommitBucketFields(renamed: true);
        templateBucketKindBox.SelectionChanged += (_, _) => CommitBucketFields();
        templateBucketCompileBox.SelectionChanged += (_, _) => CommitBucketFields();
        templateBucketTsvBox.ValueChanged += (_, _) => CommitBucketFields();
        templateBucketStartBox.TextChanged += (_, _) => CommitBucketFields();
        templateBucketSeedsBox.TextChanged += (_, _) => CommitBucketFields();
    }

    // Open the editor on a working document (a draft, clone, or duplicate). The
    // document is mutated in place as fields change; cancelling simply discards it.
    private void OpenTemplateEditor(ZetlTemplateDocument working, bool isNew)
    {
        editingTemplate = working;
        editingTemplateIsNew = isNew;
        templateErrorText.IsVisible = false;

        templateEditorUpdating = true;
        templateEditorTitle.Text = isNew ? "New Template" : $"Edit Template — {working.Name}";
        templateNameBox.Text = working.Name;
        templateCategoryBox.Text = working.Category;
        templateDescriptionBox.Text = working.Description;
        templateTypeBox.SelectedItem = working.IsConsumable
            ? ZetlTemplateTypes.Consumable
            : ZetlTemplateTypes.Capture;
        templateEditorUpdating = false;

        if (working.Buckets.Count == 0)
        {
            working.Buckets.Add(new ZetlTemplateBucketDocument { Name = "Inbox" });
        }

        RebuildTemplateBucketList(selectIndex: 0);
        ApplyTemplateSeedsVisibility();
        templateBaselineJson = CurrentTemplateJson();

        emptyState.IsVisible = false;
        projectView.IsVisible = false;
        templateEditorView.IsVisible = true;
    }

    // Serialize the working document with the current metadata-field values applied,
    // so a baseline taken at open and a later snapshot compare apples to apples.
    private string CurrentTemplateJson()
    {
        if (editingTemplate is null)
        {
            return "";
        }

        var consumable = (templateTypeBox.SelectedItem as string) == ZetlTemplateTypes.Consumable;
        var doc = ZetlTemplateDefaults.Clone(editingTemplate);
        doc.Name = templateNameBox.Text?.Trim() ?? "";
        doc.Category = string.IsNullOrWhiteSpace(templateCategoryBox.Text)
            ? "Custom"
            : templateCategoryBox.Text.Trim();
        doc.Description = templateDescriptionBox.Text?.Trim() ?? "";
        doc.Type = consumable ? ZetlTemplateTypes.Consumable : ZetlTemplateTypes.Capture;
        return JsonSerializer.Serialize(doc, JsonFile.Options);
    }

    private bool IsTemplateDirty() =>
        editingTemplate is not null && CurrentTemplateJson() != templateBaselineJson;

    private async Task CancelTemplateEditAsync()
    {
        if (IsTemplateDirty())
        {
            var discard = await KastnDialogs.ConfirmAsync(
                this,
                "Discard unsaved changes to this template?",
                "Discard");
            if (!discard)
            {
                return;
            }
        }

        CloseTemplateEditor();
    }

    private void OpenTemplateFromProject()
    {
        if (currentProject is not { } project)
        {
            return;
        }

        // Grab the project's bucket structure (names + settings), dropping reserved
        // buckets and any project-specific replay-review link. Seeds stay empty: this
        // is a capture template scaffold.
        var buckets = project.Buckets
            .Where(bucket => !ZetlTemplateValidator.ReservedName(bucket.Name))
            .Select(bucket => new ZetlTemplateBucketDocument
            {
                Name = bucket.Name,
                Settings = new ZetlBucketSettings
                {
                    Kind = bucket.Settings.DefaultKind,
                    DefaultKind = bucket.Settings.DefaultKind,
                    DefaultCompileMode = bucket.Settings.DefaultCompileMode,
                    DefaultStartingText = bucket.Settings.DefaultStartingText,
                    DefaultTsvRowLength = bucket.Settings.DefaultTsvRowLength,
                    PopMode = bucket.Settings.PopMode
                }
            })
            .ToList();
        if (buckets.Count == 0)
        {
            buckets.Add(new ZetlTemplateBucketDocument { Name = "Inbox" });
        }

        OpenTemplateEditor(
            new ZetlTemplateDocument
            {
                Name = $"{project.Name} template",
                Category = "Custom",
                Type = ZetlTemplateTypes.Capture,
                Buckets = buckets
            },
            isNew: true);
    }

    private void RebuildTemplateBucketList(int selectIndex)
    {
        templateBuckets.Clear();
        if (editingTemplate is null)
        {
            return;
        }

        foreach (var bucket in editingTemplate.Buckets)
        {
            templateBuckets.Add(new TemplateBucketItem(bucket, BucketLabel(bucket)));
        }

        if (templateBuckets.Count > 0)
        {
            templateBucketList.SelectedIndex = Math.Clamp(selectIndex, 0, templateBuckets.Count - 1);
        }
        else
        {
            OnTemplateBucketSelected();
        }
    }

    private void OnTemplateBucketSelected()
    {
        selectedTemplateBucket =
            (templateBucketList.SelectedItem as TemplateBucketItem)?.Bucket;
        var bucket = selectedTemplateBucket;
        templateBucketEditor.IsVisible = bucket is not null;
        templateBucketEmptyHint.IsVisible = bucket is null;
        if (bucket is null)
        {
            return;
        }

        templateEditorUpdating = true;
        templateBucketNameBox.Text = bucket.Name;
        templateBucketKindBox.SelectedItem = TemplateKindChoices.Contains(bucket.Settings.Kind)
            ? bucket.Settings.Kind
            : "Standard";
        templateBucketCompileBox.SelectedItem =
            TemplateCompileChoices.Contains(bucket.Settings.DefaultCompileMode)
                ? bucket.Settings.DefaultCompileMode
                : "Formatted";
        templateBucketTsvBox.Value = Math.Clamp(bucket.Settings.DefaultTsvRowLength, 1, 100);
        templateBucketStartBox.Text = bucket.Settings.DefaultStartingText;
        templateBucketSeedsBox.Text = string.Join("\n", bucket.Seeds);
        templateEditorUpdating = false;
    }

    // Write the right-panel fields back into the selected bucket document.
    private void CommitBucketFields(bool renamed = false)
    {
        if (templateEditorUpdating || selectedTemplateBucket is not { } bucket)
        {
            return;
        }

        var kind = templateBucketKindBox.SelectedItem as string ?? "Standard";
        bucket.Name = templateBucketNameBox.Text?.Trim() ?? "";
        bucket.Settings = new ZetlBucketSettings
        {
            Kind = kind,
            DefaultKind = kind,
            DefaultCompileMode = templateBucketCompileBox.SelectedItem as string ?? "Formatted",
            DefaultStartingText = templateBucketStartBox.Text ?? "",
            DefaultTsvRowLength = (int)(templateBucketTsvBox.Value ?? 5)
        };
        bucket.Seeds = ParseSeedLines(templateBucketSeedsBox.Text);

        if (renamed)
        {
            var item = templateBuckets.FirstOrDefault(
                entry => ReferenceEquals(entry.Bucket, bucket));
            if (item is not null)
            {
                item.Label = BucketLabel(bucket);
            }
        }
    }

    private void AddTemplateBucket()
    {
        if (editingTemplate is null)
        {
            return;
        }

        var bucket = new ZetlTemplateBucketDocument { Name = "" };
        editingTemplate.Buckets.Add(bucket);
        templateBuckets.Add(new TemplateBucketItem(bucket, BucketLabel(bucket)));
        templateBucketList.SelectedIndex = templateBuckets.Count - 1;
        templateBucketNameBox.Focus();
    }

    private void DeleteTemplateBucket()
    {
        if (editingTemplate is null || selectedTemplateBucket is not { } bucket)
        {
            return;
        }

        if (editingTemplate.Buckets.Count <= 1)
        {
            ShowTemplateError("A template needs at least one bucket.");
            return;
        }

        var index = editingTemplate.Buckets.IndexOf(bucket);
        editingTemplate.Buckets.Remove(bucket);
        RebuildTemplateBucketList(selectIndex: Math.Max(0, index - 1));
    }

    private void MoveTemplateBucket(int delta)
    {
        if (editingTemplate is null || selectedTemplateBucket is not { } bucket)
        {
            return;
        }

        var index = editingTemplate.Buckets.IndexOf(bucket);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= editingTemplate.Buckets.Count)
        {
            return;
        }

        editingTemplate.Buckets.RemoveAt(index);
        editingTemplate.Buckets.Insert(target, bucket);
        RebuildTemplateBucketList(selectIndex: target);
    }

    private void ApplyTemplateSeedsVisibility()
    {
        var consumable = (templateTypeBox.SelectedItem as string) == ZetlTemplateTypes.Consumable;
        templateSeedsPanel.IsVisible = consumable;
    }

    private void SaveTemplate()
    {
        if (editingTemplate is not { } template)
        {
            return;
        }

        var consumable = (templateTypeBox.SelectedItem as string) == ZetlTemplateTypes.Consumable;
        template.Name = templateNameBox.Text?.Trim() ?? "";
        template.Category = string.IsNullOrWhiteSpace(templateCategoryBox.Text)
            ? "Custom"
            : templateCategoryBox.Text.Trim();
        template.Description = templateDescriptionBox.Text?.Trim() ?? "";
        template.Type = consumable ? ZetlTemplateTypes.Consumable : ZetlTemplateTypes.Capture;
        if (!consumable)
        {
            // Capture templates never carry seeds; drop any entered while in
            // consumable mode so the document validates and stays a pure scaffold.
            foreach (var bucket in template.Buckets)
            {
                bucket.Seeds = [];
            }
        }

        if (string.IsNullOrEmpty(template.Id))
        {
            template.Id = ZetlTemplateDefaults.CreateId(template.Name);
        }

        var errors = ZetlTemplateValidator.Validate(template);
        if (errors.Count > 0)
        {
            ShowTemplateError(string.Join("\n", errors));
            return;
        }

        try
        {
            templateCatalog.Store.Save(template);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
        {
            ShowTemplateError(ex.Message);
            return;
        }

        var savedName = template.Name;
        var savedConsumable = template.IsConsumable;
        CloseTemplateEditor();
        // After saving, surface the templates tab on the matching type so the new
        // card is visible (unless a project is open, where Close returns there).
        if (currentProject is null)
        {
            landingShowingTemplates = true;
            landingShowingConsumable = savedConsumable;
            RebuildTemplateCards();
            RefreshLandingMode();
        }

        statusText.Text = $"Saved template '{savedName}'.";
    }

    private void CloseTemplateEditor()
    {
        editingTemplate = null;
        editingTemplateIsNew = false;
        selectedTemplateBucket = null;
        templateBuckets.Clear();
        templateErrorText.IsVisible = false;
        templateEditorView.IsVisible = false;

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

    private void ShowTemplateError(string message)
    {
        templateErrorText.Text = message;
        templateErrorText.IsVisible = true;
    }

    private static string BucketLabel(ZetlTemplateBucketDocument bucket) =>
        string.IsNullOrWhiteSpace(bucket.Name) ? "(unnamed bucket)" : bucket.Name;

    private static List<string> ParseSeedLines(string? text) =>
        (text ?? "")
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

    private async Task CreateProjectFromTemplateAsync(
        ZetlTemplateDocument template,
        string? defaultViewId = null)
    {
        if (!IsOnline)
        {
            statusText.Text = "Connect to Zetl before creating a project from a template.";
            return;
        }

        var name = await KastnDialogs.PromptAsync(
            this,
            $"New {template.Name} Project",
            "Project name",
            template.Name,
            candidate => projects.Any(project =>
                string.Equals(project.Name, candidate, StringComparison.OrdinalIgnoreCase))
                ? "A project with that name already exists."
                : null);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.CreateProject,
            template.ToCreateProjectCommand(name.Trim())));
        if (response.Status == ZetlResponseStatus.Success)
        {
            var created = response.Payload?.Deserialize<ZetlProjectSnapshot>(
                ZetlProtocolJson.Options);
            if (created is not null)
            {
                var allSeeded = await SeedTemplateSlipsAsync(template, created);
                if (!string.IsNullOrEmpty(defaultViewId))
                {
                    await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                        Guid.NewGuid().ToString("N"),
                        ZetlCommandKind.SetProjectView,
                        new SetProjectViewCommand { ViewId = defaultViewId },
                        created.Id,
                        expectedTargetRevision: created.MetadataRevision));
                }

                await connection.NavigateToProjectAsync(created.Id);
                statusText.Text = allSeeded
                    ? $"Created '{created.Name}' from the {template.Name} template."
                    : $"Created '{created.Name}', but some {template.Name} fields could not be added.";
                return;
            }
        }

        HandleSimpleResponse(response, $"Created a project from the {template.Name} template.");
    }

    // Seed a consumable template's ordered slips into their buckets through Zetl,
    // in listed order so a Replay bucket pastes them back in the same sequence.
    // Capture templates have no seeds and skip this entirely.
    private async Task<bool> SeedTemplateSlipsAsync(
        ZetlTemplateDocument template,
        ZetlProjectSnapshot project)
    {
        var allSeeded = true;
        foreach (var bucket in template.Buckets)
        {
            if (bucket.Seeds.Count == 0)
            {
                continue;
            }

            var target = project.Buckets.FirstOrDefault(
                item => string.Equals(item.Name, bucket.Name, StringComparison.Ordinal));
            if (target is null)
            {
                allSeeded = false;
                continue;
            }

            foreach (var text in bucket.Seeds)
            {
                var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                    Guid.NewGuid().ToString("N"),
                    ZetlCommandKind.AddSlip,
                    new AddSlipCommand
                    {
                        BucketId = target.Id,
                        Text = text,
                        Source = "template"
                    },
                    project.Id));
                if (response.Status != ZetlResponseStatus.Success)
                {
                    allSeeded = false;
                }
            }
        }

        return allSeeded;
    }

    private async Task OpenProjectCardAsync(ProjectListItem project)
    {
        if (!await SaveEditorAsync())
        {
            refreshing = true;
            landingProjectList.SelectedItem = null;
            refreshing = false;
            return;
        }

        await connection.NavigateToProjectAsync(project.Id);
    }

    private async Task CloseProjectAsync()
    {
        if (currentProject is null)
        {
            return;
        }

        if (!CanLeaveEditableView())
        {
            return;
        }

        if (!await SaveEditorAsync())
        {
            statusText.Text = "Save or resolve the current slip before closing the project.";
            return;
        }

        ExitEditableView(clearBlocks: true);
        editorState.Select(null);
        UpdateEditorFromState();
        pendingBucketSelectionId = null;
        pendingSlipSelectionId = null;
        await connection.NavigateToProjectAsync(null);
    }

    private async void OnSlipSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (refreshing)
        {
            return;
        }

        var oldId = editorState.SlipId;
        var selectedItems = SelectedSlipItems();
        if (selectedItems.Count != 1)
        {
            if (!await SaveEditorAsync())
            {
                refreshing = true;
                slipList.SelectedItem = slips.FirstOrDefault(item => item.Id == oldId);
                refreshing = false;
                return;
            }

            editorState.Select(null);
            UpdateEditorFromState();
            RefreshDestinationBuckets();
            UpdateSlipCountText();
            return;
        }

        var selected = selectedItems[0];
        if (oldId != selected.Id && !await SaveEditorAsync())
        {
            refreshing = true;
            slipList.SelectedItem = slips.FirstOrDefault(item => item.Id == oldId);
            refreshing = false;
            return;
        }

        editorState.Select(selected.Slip);
        UpdateEditorFromState();
        RefreshDestinationBuckets();
        UpdateSlipCountText();
    }

    private void OnEditorTextChanged()
    {
        if (editorUpdating || editorState.SlipId is null)
        {
            return;
        }

        editorState.SetDraft(slipEditor.Text ?? "");
        statusText.Text = editorState.IsDirty
            ? "Unsaved changes. Autosaving..."
            : connection.Current.Status;
        if (editorState.IsDirty && editorState.ConflictCurrent is null && IsOnline)
        {
            autosaveTimer.Stop();
            autosaveTimer.Start();
        }
    }

    private async Task<bool> SaveEditorAsync()
    {
        autosaveTimer.Stop();
        if (!editorState.IsDirty)
        {
            return editorState.ConflictCurrent is null;
        }

        if (!IsOnline || saving || editorState.ConflictCurrent is not null
            || currentProject is null || editorState.SlipId is null)
        {
            return false;
        }

        var text = editorState.DraftText.Trim();
        if (text.Length == 0)
        {
            if (SelectedSlip?.Source == "kastn")
            {
                text = UntitledSlipText;
            }
            else
            {
                statusText.Text = "A slip cannot be saved with empty text.";
                return false;
            }
        }

        saving = true;
        pendingSaveText = text;
        SetEditingEnabled();
        try
        {
            var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.UpdateSlip,
                new UpdateSlipCommand { Text = text },
                currentProject.Id,
                editorState.SlipId,
                editorState.Revision));
            if (response.Status == ZetlResponseStatus.Conflict)
            {
                var current = response.Conflict?.Current.Deserialize<ZetlSlipSnapshot>(
                    ZetlProtocolJson.Options);
                if (current is not null)
                {
                    editorState.Reconcile(current);
                    ShowConflict();
                }

                return false;
            }

            if (response.Status != ZetlResponseStatus.Success)
            {
                statusText.Text = response.Error?.Message ?? $"Save failed: {response.Status}.";
                return false;
            }

            var saved = response.Payload?.Deserialize<ZetlSlipSnapshot>(
                ZetlProtocolJson.Options);
            if (saved is not null)
            {
                editorState.AcceptSaved(saved);
                UpdateEditorFromState();
            }

            statusText.Text = "Slip saved.";
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            statusText.Text = $"Slip was not saved. {ex.Message}";
            return false;
        }
        finally
        {
            pendingSaveText = null;
            saving = false;
            SetEditingEnabled();
        }
    }

    private async Task AddBucketAsync()
    {
        if (!IsOnline || currentProject is null)
        {
            return;
        }

        var name = await KastnDialogs.PromptAsync(this, "New Bucket", "Bucket name");
        if (name is null)
        {
            return;
        }

        var parentId = SelectedBucketId is not null && !KastnWorkbench.IsDeletedBucket(SelectedBucket)
            ? SelectedBucketId
            : null;
        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.AddBucket,
            new AddBucketCommand
            {
                Name = name,
                ParentBucketId = parentId
            },
            currentProject.Id));
        if (response.Status == ZetlResponseStatus.Success)
        {
            pendingBucketSelectionId = response.Payload?.Deserialize<ZetlBucketSnapshot>(
                ZetlProtocolJson.Options)?.Id;
            await connection.RefreshAsync();
            statusText.Text = $"Bucket '{name}' created.";
        }
        else
        {
            statusText.Text = response.Error?.Message ?? $"Bucket creation failed: {response.Status}.";
        }
    }

    private async Task AddSlipAsync()
    {
        if (!IsOnline || currentProject is null)
        {
            return;
        }

        if (!await SaveEditorAsync())
        {
            statusText.Text = "Save or resolve the current slip before creating a new one.";
            return;
        }

        var destinationBucketId = SelectedBucketId
            is { } selectedBucketId && !KastnWorkbench.IsDeletedBucket(SelectedBucket)
                ? selectedBucketId
                : null;
        destinationBucketId ??= currentProject.ActiveBucketId
            ?? currentProject.Buckets.FirstOrDefault(
                bucket => !KastnWorkbench.IsDeletedBucket(bucket))?.Id;
        if (destinationBucketId is null)
        {
            statusText.Text = "Create a bucket before adding a slip.";
            return;
        }

        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.AddSlip,
            new AddSlipCommand
            {
                BucketId = destinationBucketId,
                Text = UntitledSlipText,
                Source = "kastn"
            },
            currentProject.Id));
        if (response.Status == ZetlResponseStatus.Success)
        {
            var created = response.Payload?.Deserialize<ZetlSlipSnapshot>(
                ZetlProtocolJson.Options);
            if (created is not null)
            {
                pendingBucketSelectionId = created.BucketId;
                pendingSlipSelectionId = created.Id;
                pendingSlipFocus = false;
                pendingEditableBlockFocusId = created.Id;
                ResetSlipFilters();
                await connection.RefreshAsync();
                if (viewerMode)
                {
                    editableViewMode = true;
                    viewerTextBox.IsVisible = false;
                    editViewScroll.IsVisible = true;
                    BuildEditableViewBlocks(CurrentFilteredSlips());
                }

                statusText.Text = "Slip created.";
                return;
            }
        }

        HandleSimpleResponse(response, "Slip created.");
    }

    private async Task SaveBucketAsync()
    {
        if (!IsOnline || currentProject is null || SelectedBucket is not { } bucket)
        {
            return;
        }

        var name = bucketNameBox.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            statusText.Text = "A bucket name is required.";
            return;
        }

        var parentId = (parentBucketBox.SelectedItem as KastnBucketItem)?.Id;
        pendingBucketSelectionId = bucket.Id;
        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.UpdateBucket,
            new UpdateBucketCommand
            {
                Name = name,
                ParentBucketId = parentId,
                Settings = bucket.Settings
            },
            currentProject.Id,
            bucket.Id,
            bucket.Revision));
        if (response.Status == ZetlResponseStatus.Success)
        {
            await connection.RefreshAsync();
        }

        HandleSimpleResponse(response, "Bucket saved.");
    }

    private async Task DeleteBucketAsync()
    {
        if (!IsOnline || currentProject is null || SelectedBucket is not { } bucket)
        {
            return;
        }

        if (!await KastnDialogs.ConfirmAsync(
                this,
                $"Delete '{bucket.Name}' and all of its child buckets and slips?",
                "Delete Bucket"))
        {
            return;
        }

        pendingBucketSelectionId = bucket.ParentBucketId;
        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.DeleteBucket,
            new DeleteBucketCommand(),
            currentProject.Id,
            bucket.Id,
            bucket.Revision));
        if (response.Status == ZetlResponseStatus.Success)
        {
            editorState.Select(null);
            UpdateEditorFromState();
            await connection.RefreshAsync();
        }

        HandleSimpleResponse(response, "Bucket deleted.");
    }

    private async Task DeleteProjectAsync()
    {
        if (!IsOnline || currentProject is null)
        {
            return;
        }

        await DeleteProjectAsync(new ProjectListItem(
            currentProject.Id,
            currentProject.Name,
            currentProject.MetadataRevision,
            "",
            "",
            ""));
    }

    private async Task RenameProjectAsync(ProjectListItem project)
    {
        if (!IsOnline)
        {
            return;
        }

        if (!await SaveEditorAsync())
        {
            statusText.Text = "Save or resolve the current slip before renaming the project.";
            return;
        }

        var name = await KastnDialogs.PromptAsync(
            this,
            "Rename Project",
            "Project name",
            project.Name);
        if (name is null || string.Equals(name, project.Name, StringComparison.Ordinal))
        {
            return;
        }

        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.RenameProject,
            new RenameProjectCommand { Name = name },
            project.Id,
            project.Id,
            project.MetadataRevision));
        if (response.Status == ZetlResponseStatus.Success)
        {
            await connection.RefreshAsync();
        }

        HandleSimpleResponse(response, $"Project renamed to '{name}'.");
    }

    private async Task DeleteProjectAsync(ProjectListItem project)
    {
        if (!IsOnline)
        {
            return;
        }

        if (!await SaveEditorAsync())
        {
            statusText.Text = "Save or resolve the current slip before deleting the project.";
            return;
        }

        if (!await KastnDialogs.ConfirmAsync(
                this,
                $"Delete project '{project.Name}' and all of its buckets and slips? This cannot be undone.",
                "Delete Project"))
        {
            return;
        }

        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.DeleteProject,
            new DeleteProjectCommand(),
            project.Id,
            project.Id,
            project.MetadataRevision));
        if (response.Status == ZetlResponseStatus.Success)
        {
            if (string.Equals(currentProject?.Id, project.Id, StringComparison.Ordinal))
            {
                editorState.Select(null);
                UpdateEditorFromState();
                await connection.NavigateToProjectAsync(null);
            }

            await connection.RefreshAsync();
        }

        HandleSimpleResponse(response, "Project deleted.");
    }

    private async Task MoveSlipAsync()
    {
        if (!await SaveEditorAsync()
            || !IsOnline
            || currentProject is null
            || moveBucketBox.SelectedItem is not KastnBucketItem destination
            || destination.Id is null)
        {
            return;
        }

        var selected = SelectedSlips()
            .Where(slip => !IsSlipInDeleted(slip))
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var moved = 0;
        var skipped = 0;
        var failed = 0;
        var projectId = currentProject.Id;
        pendingBucketSelectionId = destination.Id;
        foreach (var slip in selected)
        {
            if (slip.BucketId == destination.Id)
            {
                skipped++;
                continue;
            }

            var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.MoveSlip,
                new MoveSlipCommand { DestinationBucketId = destination.Id },
                projectId,
                slip.Id,
                slip.Revision));
            if (response.Status == ZetlResponseStatus.Success)
            {
                moved++;
            }
            else
            {
                failed++;
            }
        }

        editorState.Select(null);
        UpdateEditorFromState();
        await connection.RefreshAsync();
        statusText.Text = BatchStatus(
            moved > 0 ? $"{moved} slip{Plural(moved)} moved to {destination.Bucket?.Name}" : null,
            skipped > 0 ? $"{skipped} already there" : null,
            failed > 0 ? $"{failed} failed" : null);
    }

    private async Task RestoreSlipAsync()
    {
        if (!await SaveEditorAsync()
            || !IsOnline
            || currentProject is null
            || SelectedSlip is not { } slip
            || !IsSlipInDeleted(slip))
        {
            return;
        }

        var destination = moveBucketBox.SelectedItem as KastnBucketItem
            ?? moveBuckets.FirstOrDefault();
        if (destination?.Id is null)
        {
            statusText.Text = "Create a regular bucket before restoring this slip.";
            return;
        }

        pendingBucketSelectionId = destination.Id;
        pendingSlipSelectionId = slip.Id;
        pendingSlipFocus = false;
        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.MoveSlip,
            new MoveSlipCommand { DestinationBucketId = destination.Id },
            currentProject.Id,
            slip.Id,
            editorState.Revision));
        HandleSimpleResponse(response, $"Slip restored to {destination.Bucket?.Name}.");
    }

    private async Task DeleteSlipAsync()
    {
        if (!await SaveEditorAsync()
            || !IsOnline
            || currentProject is null
            || SelectedSlips().Count == 0)
        {
            return;
        }

        var selected = SelectedSlips()
            .Where(slip => !IsSlipInDeleted(slip))
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        if (!await KastnDialogs.ConfirmAsync(
                this,
                selected.Count == 1
                    ? "Move the selected slip to Deleted?"
                    : $"Move {selected.Count} selected slips to Deleted?",
                "Delete Slip"))
        {
            return;
        }

        var moved = 0;
        var failed = 0;
        var projectId = currentProject.Id;
        foreach (var slip in selected)
        {
            var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.DeleteSlip,
                new DeleteSlipCommand(),
                projectId,
                slip.Id,
                slip.Revision));
            if (response.Status == ZetlResponseStatus.Success)
            {
                moved++;
                var deleted = response.Payload?.Deserialize<ZetlSlipSnapshot>(
                    ZetlProtocolJson.Options);
                pendingBucketSelectionId ??= deleted?.BucketId;
            }
            else
            {
                failed++;
            }
        }

        editorState.Select(null);
        ResetSlipFilters();
        UpdateEditorFromState();
        await connection.RefreshAsync();
        statusText.Text = failed == 0
            ? $"{moved} slip{Plural(moved)} moved to Deleted."
            : $"{moved} slip{Plural(moved)} moved to Deleted; {failed} failed.";
    }

    private void UseZetlVersion()
    {
        editorState.UseCurrent();
        UpdateEditorFromState();
        statusText.Text = "Using the current Zetl version.";
    }

    private async Task KeepMineAsync()
    {
        editorState.PrepareOverwrite();
        UpdateEditorFromState();
        editorState.SetDraft(localConflictText.Text ?? "");
        editorUpdating = true;
        slipEditor.Text = editorState.DraftText;
        editorUpdating = false;
        await SaveEditorAsync();
    }

    private void ShowConflict()
    {
        conflictPanel.IsVisible = editorState.ConflictCurrent is not null;
        localConflictText.Text = editorState.DraftText;
        remoteConflictText.Text = editorState.ConflictCurrent?.Text ?? "";
        statusText.Text = "Resolve the slip conflict before continuing.";
        SetEditingEnabled();
    }

    private void FocusMainView()
    {
        if (editableViewMode)
        {
            editableViewBlocks.FirstOrDefault()?.Editor?.Focus();
            return;
        }

        viewerTextBox.Focus();
    }

    private void UpdateEditorFromState()
    {
        var selectedSlips = SelectedSlips();
        if (selectedSlips.Count > 1)
        {
            editorUpdating = true;
            slipEditor.Text = "";
            editorUpdating = false;
            conflictPanel.IsVisible = false;
            slipMetadataText.Text = $"{selectedSlips.Count} slips selected. "
                + "Choose a destination, then move or delete them together.";
            SetEditingEnabled();
            return;
        }

        editorUpdating = true;
        slipEditor.Text = SelectedSlip is { } editorSlip
            && IsUntitledKastnSlip(editorSlip)
            && !editorState.IsDirty
                ? ""
                : editorState.DraftText;
        editorUpdating = false;
        conflictPanel.IsVisible = editorState.ConflictCurrent is not null;
        if (editorState.ConflictCurrent is { } conflict)
        {
            localConflictText.Text = editorState.DraftText;
            remoteConflictText.Text = conflict.Text;
        }

        var slip = SelectedSlip
            ?? currentProject?.Slips.FirstOrDefault(item => item.Id == editorState.SlipId);
        slipMetadataText.Text = slip is null
            ? "Select a slip to read or edit it."
            : SlipMetadata(slip);
        SetEditingEnabled();
    }

    private void SetEditingEnabled()
    {
        var selectedSlips = SelectedSlips();
        var hasSelectedSlips = selectedSlips.Count > 0;
        var hasMultipleSelectedSlips = selectedSlips.Count > 1;
        var allSelectedSlipsAreActive = hasSelectedSlips
            && selectedSlips.All(slip => !IsSlipInDeleted(slip));
        var selectedSlipIsDeleted = selectedSlips.Count == 1 && IsSlipInDeleted(selectedSlips[0]);
        var canEdit = IsOnline
            && editorState.SlipId is not null
            && !hasMultipleSelectedSlips
            && !saving;
        var canBatch = IsOnline
            && hasSelectedSlips
            && !saving
            && editorState.ConflictCurrent is null;
        var canCreateSlip = IsOnline
            && currentProject is not null
            && !KastnWorkbench.IsDeletedBucket(SelectedBucket);
        slipEditor.IsEnabled = !viewerMode && canEdit && editorState.ConflictCurrent is null;
        saveSlipButton.IsEnabled = !viewerMode && canEdit && editorState.ConflictCurrent is null;
        saveSlipMenuItem.IsEnabled = false;
        deleteSlipButton.IsEnabled = !viewerMode && canBatch && allSelectedSlipsAreActive;
        deleteSlipMenuItem.IsEnabled = false;
        restoreSlipButton.IsEnabled = !viewerMode && canEdit && selectedSlipIsDeleted && moveBuckets.Count > 0;
        moveSlipButton.IsEnabled = !viewerMode && canBatch && allSelectedSlipsAreActive && moveBuckets.Count > 0;
        moveBucketBox.IsEnabled = moveSlipButton.IsEnabled || restoreSlipButton.IsEnabled;
        addBucketButton.IsEnabled = IsOnline && currentProject is not null;
        closeProjectButton.IsEnabled = currentProject is not null;
        newSlipButton.IsEnabled = canCreateSlip && !savingEditableView;
        newSlipMenuItem.IsEnabled = newSlipButton.IsEnabled;
        viewerModeMenuItem.IsEnabled = currentProject is not null;
        viewerModeMenuItem.Header = editableViewMode ? "_Read View" : "_Edit View";
        RefreshEditableViewStatus();
    }

    private void SetConnectionState(KastnSessionSnapshot snapshot)
    {
        statusText.Text = editorState.IsDirty
            ? "Unsaved changes."
            : snapshot.Status;
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
        focusProjectsMenuItem.IsEnabled = landingProjectList.IsVisible;
        newSlipMenuItem.IsEnabled = online
            && currentProject is not null
            && !KastnWorkbench.IsDeletedBucket(SelectedBucket);
        SetEditingEnabled();
        RefreshBucketEditor();
    }

    private async Task RefreshAsync()
    {
        try
        {
            await SaveEditorAsync();
            await connection.RefreshAsync();
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            statusText.Text = ex.Message;
        }
    }

    private void HandleSimpleResponse(ZetlResponseEnvelope response, string success)
    {
        if (response.Status == ZetlResponseStatus.Conflict
            && response.Conflict?.TargetKind == ZetlEntityKind.Slip)
        {
            var current = response.Conflict.Current.Deserialize<ZetlSlipSnapshot>(
                ZetlProtocolJson.Options);
            if (current is not null)
            {
                editorState.Reconcile(current);
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
    }

    private async void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.F5)
        {
            args.Handled = true;
            await RefreshAsync();
        }
        else if (args.KeyModifiers.HasFlag(KeyModifiers.Control) && args.Key == Key.F)
        {
            args.Handled = true;
            searchBox.Focus();
            searchBox.SelectAll();
        }
        else if (args.KeyModifiers.HasFlag(KeyModifiers.Control) && args.Key == Key.S)
        {
            args.Handled = true;
            await SaveEditorAsync();
        }
        else if (args.KeyModifiers.HasFlag(KeyModifiers.Control) && args.Key == Key.W)
        {
            args.Handled = true;
            await CloseProjectAsync();
        }
        else if (args.KeyModifiers.HasFlag(KeyModifiers.Control)
            && args.KeyModifiers.HasFlag(KeyModifiers.Shift)
            && args.Key == Key.E)
        {
            args.Handled = true;
            await ToggleEditableViewAsync();
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
        connection.Current.ConnectionState == KastnConnectionState.Online;

    private string? SelectedBucketId =>
        (bucketList.SelectedItem as KastnBucketItem)?.Id;

    private ZetlBucketSnapshot? SelectedBucket =>
        (bucketList.SelectedItem as KastnBucketItem)?.Bucket;

    private IReadOnlyList<ZetlSlipSnapshot> CurrentFilteredSlips()
    {
        return currentProject is null
            ? []
            : KastnWorkbench.FilterSlips(
                currentProject,
                SelectedBucketId,
                (sourceFilterBox.SelectedItem as FilterItem)?.Value,
                (sessionFilterBox.SelectedItem as FilterItem)?.Value,
                (dateFilterBox.SelectedItem as DateFilterItem)?.Value ?? KastnDateFilter.All,
                searchBox.Text,
                DateTimeOffset.Now);
    }

    private void RefreshViewer()
    {
        if (currentProject is null)
        {
            viewerSummaryText.Text = "No project selected.";
            viewerTextBox.Text = "";
            ExitEditableView(clearBlocks: true);
            return;
        }

        var visible = CurrentFilteredSlips();
        viewerSummaryText.Text = visible.Count == 0
            ? "No slips match the current filters."
            : $"{visible.Count} of {currentProject.Slips.Count} slips in the current view.";
        if (editableViewMode)
        {
            if (!savingEditableView && !HasDirtyEditableBlocks())
            {
                BuildEditableViewBlocks(visible);
            }

            RefreshEditableViewStatus();
            return;
        }

        if (SelectedView.Kind == ZetlViewKinds.Pdf)
        {
            // PDF is binary — there is no inline text to show or copy; the preview
            // explains how to get it, and Export writes the .pdf.
            lastRenderedViewText = "";
            viewerTextBox.Text = visible.Count == 0
                ? ""
                : "PDF view — use Export to save a .pdf of the current slips.";
            copyViewButton.IsEnabled = false;
            exportViewButton.IsEnabled = visible.Count > 0;
        }
        else
        {
            lastRenderedViewText = visible.Count == 0
                ? ""
                : ZetlViewRenderer.Render(currentProject, visible, SelectedView);
            viewerTextBox.Text = lastRenderedViewText;
            var hasOutput = lastRenderedViewText.Length > 0;
            copyViewButton.IsEnabled = hasOutput;
            exportViewButton.IsEnabled = hasOutput;
        }

        deleteViewMenuItem.IsEnabled = !ZetlViewDefaults.IsBuiltIn(SelectedView.Id);
        RefreshEditableViewStatus();
    }

    private ZetlViewDocument SelectedView =>
        viewPickerBox.SelectedItem as ZetlViewDocument
        ?? (loadedViews.Count > 0 ? loadedViews[0] : ZetlViewDefaults.CreateAll()[0]);

    private void SelectViewForProject(ZetlProjectSnapshot project)
    {
        var target = string.IsNullOrEmpty(project.DefaultViewId)
            ? null
            : loadedViews.FirstOrDefault(view => view.Id == project.DefaultViewId);
        viewPickerBox.SelectedItem = target ?? loadedViews.FirstOrDefault();
    }

    private async Task CopyRenderedViewAsync()
    {
        if (lastRenderedViewText.Length == 0 || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(lastRenderedViewText);
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
            await using var stream = await file.OpenWriteAsync();
            if (isPdf)
            {
                var pdf = KastnPdfRenderer.Render(currentProject, CurrentFilteredSlips(), view);
                await stream.WriteAsync(pdf);
            }
            else
            {
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(lastRenderedViewText);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
        viewErrorText.IsVisible = false;

        viewEditorUpdating = true;
        viewEditorTitle.Text = isNew ? "New View" : $"Edit View — {working.Name}";
        viewNameBox.Text = working.Name;
        viewCategoryBox.Text = working.Category;
        viewDescriptionBox.Text = working.Description;
        viewKindBox.SelectedItem = ViewKindChoices.Contains(working.Kind)
            ? working.Kind
            : ZetlViewKinds.Formatted;
        viewTsvRowBox.Value = Math.Clamp(working.TsvRowLength, 1, 100);
        viewSectionsBox.Text = SectionsToText(working.Sections);
        viewEditorUpdating = false;

        ApplyViewKindTsvVisibility();
        viewBaselineJson = CurrentViewJson();

        emptyState.IsVisible = false;
        projectView.IsVisible = false;
        templateEditorView.IsVisible = false;
        viewEditorView.IsVisible = true;
    }

    private void ApplyViewKindTsvVisibility()
    {
        viewTsvPanel.IsVisible = (viewKindBox.SelectedItem as string) == ZetlViewKinds.Tsv;
    }

    private string CurrentViewJson()
    {
        if (editingView is null)
        {
            return "";
        }

        var doc = ZetlViewDefaults.Clone(editingView);
        doc.Name = viewNameBox.Text?.Trim() ?? "";
        doc.Category = string.IsNullOrWhiteSpace(viewCategoryBox.Text)
            ? "Custom"
            : viewCategoryBox.Text.Trim();
        doc.Description = viewDescriptionBox.Text?.Trim() ?? "";
        doc.Kind = viewKindBox.SelectedItem as string ?? ZetlViewKinds.Formatted;
        doc.TsvRowLength = (int)(viewTsvRowBox.Value ?? 5);
        doc.Sections = ParseSections(viewSectionsBox.Text);
        return JsonSerializer.Serialize(doc, JsonFile.Options);
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

    private void SaveView()
    {
        if (editingView is not { } view)
        {
            return;
        }

        view.Name = viewNameBox.Text?.Trim() ?? "";
        view.Category = string.IsNullOrWhiteSpace(viewCategoryBox.Text)
            ? "Custom"
            : viewCategoryBox.Text.Trim();
        view.Description = viewDescriptionBox.Text?.Trim() ?? "";
        view.Kind = viewKindBox.SelectedItem as string ?? ZetlViewKinds.Formatted;
        view.TsvRowLength = (int)(viewTsvRowBox.Value ?? 5);
        view.Sections = ParseSections(viewSectionsBox.Text);
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

        var savedId = view.Id;
        var savedName = view.Name;
        CloseViewEditor();
        ReloadViews(savedId);
        statusText.Text = $"Saved view '{savedName}'.";
    }

    private void CloseViewEditor()
    {
        editingView = null;
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

    // Parse the compact section spec: one section per line, "Heading = Bucket, Bucket".
    // A line without '=' is shorthand for a section named after a single bucket.
    private static List<ZetlViewSection> ParseSections(string? text)
    {
        var sections = new List<ZetlViewSection>();
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var equals = line.IndexOf('=');
            string title;
            List<string> buckets;
            if (equals >= 0)
            {
                title = line[..equals].Trim();
                buckets = line[(equals + 1)..]
                    .Split(',')
                    .Select(part => part.Trim())
                    .Where(part => part.Length > 0)
                    .ToList();
            }
            else
            {
                title = line;
                buckets = [line];
            }

            if (title.Length > 0 && buckets.Count > 0)
            {
                sections.Add(new ZetlViewSection { Title = title, Buckets = buckets });
            }
        }

        return sections;
    }

    private static string SectionsToText(IReadOnlyList<ZetlViewSection> sections)
    {
        return string.Join("\n", sections.Select(section =>
            section.Buckets.Count == 1
                && string.Equals(section.Buckets[0], section.Title, StringComparison.OrdinalIgnoreCase)
                ? section.Title
                : $"{section.Title} = {string.Join(", ", section.Buckets)}"));
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

        try
        {
            viewStore.Delete(view.Id);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            statusText.Text = $"Could not delete view: {ex.Message}";
            return;
        }

        ReloadViews(null);
        statusText.Text = $"Deleted view '{view.Name}'.";
    }

    // Re-read the view catalog from disk, restoring the selection (by id when given,
    // otherwise the first view) and re-rendering the read view.
    private void ReloadViews(string? selectId)
    {
        loadedViews = viewStore.LoadAll();
        viewPickerBox.ItemsSource = loadedViews;
        var target = selectId is null
            ? null
            : loadedViews.FirstOrDefault(view => view.Id == selectId);
        viewPickerBox.SelectedItem = target ?? loadedViews.FirstOrDefault();
        RefreshViewer();
    }

    // ---- Creation types (template + default view) ----

    private void WireCreationEditor()
    {
        saveCreationButton.Click += (_, _) => SaveCreation();
        cancelCreationButton.Click += async (_, _) => await CancelCreationEditAsync();
    }

    private void RebuildCreationCards()
    {
        var loaded = creationStore.LoadAll();
        var templatesById = templateCatalog.LoadAll().ToDictionary(t => t.Id, StringComparer.Ordinal);
        creations.Clear();
        foreach (var creation in loaded)
        {
            var templateName = templatesById.TryGetValue(creation.TemplateId, out var t)
                ? t.Name
                : creation.TemplateId;
            var viewName = creation.PrimaryViewId is { } viewId
                ? loadedViews.FirstOrDefault(v => v.Id == viewId)?.Name ?? viewId
                : "no view";
            creations.Add(new CreationListItem(
                creation.Category,
                creation.Name,
                $"Template: {templateName}  ·  View: {viewName}",
                creation,
                !ZetlCreationTypeDefaults.IsBuiltIn(creation.Id)));
        }

        RefreshLandingGridLayout();
    }

    private async void OnUseCreationClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is not CreationListItem creation)
        {
            return;
        }

        if (!IsOnline)
        {
            statusText.Text = "Connect to Zetl before using a creation type.";
            return;
        }

        var template = templateCatalog.LoadAll()
            .FirstOrDefault(t => t.Id == creation.Source.TemplateId);
        if (template is null)
        {
            statusText.Text = $"The '{creation.Name}' template is missing.";
            return;
        }

        await CreateProjectFromTemplateAsync(template, creation.Source.PrimaryViewId);
    }

    private void OnNewCreationClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        OpenCreationEditor(new ZetlCreationTypeDocument { Name = "", Category = "Custom" }, isNew: true);
    }

    private void OnEditCreationClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is CreationListItem creation)
        {
            if (ZetlCreationTypeDefaults.IsBuiltIn(creation.Source.Id))
            {
                OpenCreationEditor(ZetlCreationTypeDefaults.Duplicate(creation.Source), isNew: true);
            }
            else
            {
                OpenCreationEditor(ZetlCreationTypeDefaults.Clone(creation.Source), isNew: false);
            }
        }
    }

    private void OnDuplicateCreationClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is CreationListItem creation)
        {
            OpenCreationEditor(ZetlCreationTypeDefaults.Duplicate(creation.Source), isNew: true);
        }
    }

    private async void OnDeleteCreationClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is not CreationListItem creation)
        {
            return;
        }

        var confirmed = await KastnDialogs.ConfirmAsync(
            this,
            $"Delete the creation type '{creation.Name}'? This cannot be undone.",
            "Delete");
        if (!confirmed)
        {
            return;
        }

        try
        {
            creationStore.Delete(creation.Source.Id);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            statusText.Text = $"Could not delete creation type: {ex.Message}";
            return;
        }

        RebuildCreationCards();
        RefreshLandingMode();
        statusText.Text = $"Deleted creation type '{creation.Name}'.";
    }

    private void OpenCreationEditor(ZetlCreationTypeDocument working, bool isNew)
    {
        editingCreation = working;
        creationErrorText.IsVisible = false;

        var templates = templateCatalog.LoadAll();
        var viewChoices = new List<ZetlViewDocument> { NoView };
        viewChoices.AddRange(viewStore.LoadAll());

        creationEditorTitle.Text = isNew ? "New Creation Type" : $"Edit Creation Type — {working.Name}";
        creationNameBox.Text = working.Name;
        creationCategoryBox.Text = working.Category;
        creationDescriptionBox.Text = working.Description;
        creationTemplateBox.ItemsSource = templates;
        creationTemplateBox.SelectedItem =
            templates.FirstOrDefault(t => t.Id == working.TemplateId) ?? templates.FirstOrDefault();
        creationViewBox.ItemsSource = viewChoices;
        creationViewBox.SelectedItem = working.PrimaryViewId is { } viewId
            ? viewChoices.FirstOrDefault(v => v.Id == viewId) ?? NoView
            : NoView;

        creationBaselineJson = CurrentCreationJson();

        emptyState.IsVisible = false;
        projectView.IsVisible = false;
        creationEditorView.IsVisible = true;
    }

    private string CurrentCreationJson()
    {
        if (editingCreation is null)
        {
            return "";
        }

        var doc = ZetlCreationTypeDefaults.Clone(editingCreation);
        doc.Name = creationNameBox.Text?.Trim() ?? "";
        doc.Category = string.IsNullOrWhiteSpace(creationCategoryBox.Text)
            ? "Custom"
            : creationCategoryBox.Text.Trim();
        doc.Description = creationDescriptionBox.Text?.Trim() ?? "";
        doc.TemplateId = (creationTemplateBox.SelectedItem as ZetlTemplateDocument)?.Id ?? "";
        var view = creationViewBox.SelectedItem as ZetlViewDocument;
        doc.ViewIds = view is null || string.IsNullOrEmpty(view.Id) ? [] : [view.Id];
        return JsonSerializer.Serialize(doc, JsonFile.Options);
    }

    private bool IsCreationDirty() =>
        editingCreation is not null && CurrentCreationJson() != creationBaselineJson;

    private async Task CancelCreationEditAsync()
    {
        if (IsCreationDirty())
        {
            var discard = await KastnDialogs.ConfirmAsync(
                this,
                "Discard unsaved changes to this creation type?",
                "Discard");
            if (!discard)
            {
                return;
            }
        }

        CloseCreationEditor();
    }

    private void SaveCreation()
    {
        if (editingCreation is not { } creation)
        {
            return;
        }

        creation.Name = creationNameBox.Text?.Trim() ?? "";
        creation.Category = string.IsNullOrWhiteSpace(creationCategoryBox.Text)
            ? "Custom"
            : creationCategoryBox.Text.Trim();
        creation.Description = creationDescriptionBox.Text?.Trim() ?? "";
        creation.TemplateId = (creationTemplateBox.SelectedItem as ZetlTemplateDocument)?.Id ?? "";
        var view = creationViewBox.SelectedItem as ZetlViewDocument;
        creation.ViewIds = view is null || string.IsNullOrEmpty(view.Id) ? [] : [view.Id];
        if (string.IsNullOrEmpty(creation.Id))
        {
            creation.Id = ZetlCreationTypeDefaults.CreateId(creation.Name);
        }

        var errors = ZetlCreationTypeValidator.Validate(creation);
        if (errors.Count > 0)
        {
            creationErrorText.Text = string.Join("\n", errors);
            creationErrorText.IsVisible = true;
            return;
        }

        try
        {
            creationStore.Save(creation);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
        {
            creationErrorText.Text = ex.Message;
            creationErrorText.IsVisible = true;
            return;
        }

        var savedName = creation.Name;
        CloseCreationEditor();
        if (currentProject is null)
        {
            landingShowingTemplates = false;
            landingShowingCreations = true;
            RebuildCreationCards();
            RefreshLandingMode();
        }

        statusText.Text = $"Saved creation type '{savedName}'.";
    }

    private void CloseCreationEditor()
    {
        editingCreation = null;
        creationErrorText.IsVisible = false;
        creationEditorView.IsVisible = false;
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

    private async Task ToggleEditableViewAsync()
    {
        if (!viewerMode || currentProject is null)
        {
            return;
        }

        if (!editableViewMode)
        {
            if (!await SaveEditorAsync())
            {
                statusText.Text = "Save or resolve the current slip before opening edit view.";
                return;
            }

            editableViewMode = true;
            viewerTextBox.IsVisible = false;
            editViewScroll.IsVisible = true;
            BuildEditableViewBlocks(CurrentFilteredSlips());
            editableBlocksPanel.Focus();
            return;
        }

        if (HasDirtyEditableBlocks())
        {
            statusText.Text = "Save All or Cancel before leaving edit view.";
            RefreshEditableViewStatus();
            return;
        }

        ExitEditableView(clearBlocks: true);
        RefreshViewer();
    }

    private async Task SaveEditableViewAsync()
    {
        if (!editableViewMode || !IsOnline || currentProject is null || savingEditableView)
        {
            return;
        }

        var dirty = editableViewBlocks
            .Where(block => block.IsDirty)
            .ToList();
        if (dirty.Count == 0)
        {
            statusText.Text = "Edit view has no unsaved slips.";
            RefreshEditableViewStatus();
            return;
        }

        savingEditableView = true;
        SetEditingEnabled();
        try
        {
            var saved = 0;
            var failed = 0;
            var projectId = currentProject.Id;
            foreach (var block in dirty)
            {
                var text = block.DraftText.Trim();
                if (text.Length == 0)
                {
                    block.Status = "Text is required.";
                    failed++;
                    continue;
                }

                var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                    Guid.NewGuid().ToString("N"),
                    ZetlCommandKind.UpdateSlip,
                    new UpdateSlipCommand { Text = text },
                    projectId,
                    block.SlipId,
                    block.Revision));
                if (response.Status == ZetlResponseStatus.Success)
                {
                    var savedSlip = response.Payload?.Deserialize<ZetlSlipSnapshot>(
                        ZetlProtocolJson.Options);
                    if (savedSlip is not null)
                    {
                        block.Accept(savedSlip);
                    }

                    block.Status = "Saved.";
                    saved++;
                    continue;
                }

                if (response.Status == ZetlResponseStatus.Conflict)
                {
                    var current = response.Conflict?.Current.Deserialize<ZetlSlipSnapshot>(
                        ZetlProtocolJson.Options);
                    block.ConflictText = current?.Text;
                    block.Status = "Conflict: reload the project or copy your draft before retrying.";
                }
                else
                {
                    block.Status = response.Error?.Message ?? $"Save failed: {response.Status}.";
                }

                failed++;
            }

            RenderEditableViewBlocks();
            statusText.Text = BatchStatus(
                saved > 0 ? $"{saved} slip{Plural(saved)} saved" : null,
                failed > 0 ? $"{failed} failed" : null);
            await connection.RefreshAsync();
        }
        finally
        {
            savingEditableView = false;
            SetEditingEnabled();
            RefreshEditableViewStatus();
        }
    }

    private async Task MoveSelectedEditableBlocksAsync()
    {
        if (!editableViewMode
            || !IsOnline
            || currentProject is null
            || editViewMoveBucketBox.SelectedItem is not KastnBucketItem destination
            || destination.Id is null)
        {
            return;
        }

        if (HasDirtyEditableBlocks())
        {
            statusText.Text = "Save All or Cancel before moving edit blocks.";
            RefreshEditableViewStatus();
            return;
        }

        var selected = editableViewBlocks
            .Where(block => block.IsSelected)
            .ToList();
        if (selected.Count == 0)
        {
            statusText.Text = "Select one or more edit blocks to move.";
            return;
        }

        var moved = 0;
        var failed = 0;
        var projectId = currentProject.Id;
        pendingBucketSelectionId = destination.Id;
        savingEditableView = true;
        SetEditingEnabled();
        try
        {
            foreach (var block in selected)
            {
                var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                    Guid.NewGuid().ToString("N"),
                    ZetlCommandKind.MoveSlip,
                    new MoveSlipCommand { DestinationBucketId = destination.Id },
                    projectId,
                    block.SlipId,
                    block.Revision));
                if (response.Status == ZetlResponseStatus.Success)
                {
                    moved++;
                }
                else
                {
                    block.Status = response.Error?.Message ?? $"Move failed: {response.Status}.";
                    failed++;
                }
            }

            await connection.RefreshAsync();
            statusText.Text = BatchStatus(
                moved > 0 ? $"{moved} slip{Plural(moved)} moved to {destination.Bucket?.Name}" : null,
                failed > 0 ? $"{failed} failed" : null);
        }
        finally
        {
            savingEditableView = false;
            SetEditingEnabled();
            RefreshEditableViewStatus();
        }
    }

    private void CancelEditableView()
    {
        if (!editableViewMode)
        {
            return;
        }

        ExitEditableView(clearBlocks: true);
        RefreshViewer();
        statusText.Text = "Edit view canceled.";
    }

    private void BuildEditableViewBlocks(IReadOnlyList<ZetlSlipSnapshot> visible)
    {
        editableViewBlocks.Clear();
        if (currentProject is not null)
        {
            foreach (var slip in visible)
            {
                var bucket = currentProject.Buckets.FirstOrDefault(
                    bucket => bucket.Id == slip.BucketId);
                editableViewBlocks.Add(new EditableSlipBlock(
                    slip,
                    bucket is null
                        ? "Unknown bucket"
                        : KastnWorkbench.BucketPathLabel(currentProject, bucket)));
            }
        }

        RenderEditableViewBlocks();
        RefreshEditableViewStatus();
    }

    private void RenderEditableViewBlocks()
    {
        editableBlocksPanel.Children.Clear();
        var total = editableViewBlocks.Count;
        var reorderable = total > 1 && EditViewBlocksShareBucket();
        for (var index = 0; index < editableViewBlocks.Count; index++)
        {
            var block = editableViewBlocks[index];
            var statusTextBlock = new TextBlock
            {
                [Grid.ColumnProperty] = 1,
                FontSize = 13,
                Text = EditableBlockStatus(block)
            };
            block.StatusText = statusTextBlock;

            var selectionBox = new CheckBox
            {
                IsChecked = block.IsSelected,
                VerticalAlignment = VerticalAlignment.Center
            };
            selectionBox.IsCheckedChanged += (_, _) =>
            {
                block.IsSelected = selectionBox.IsChecked == true;
                RefreshEditableViewStatus();
            };

            var textBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 90,
                Text = block.DraftText,
                IsEnabled = IsOnline && block.ConflictText is null
            };
            block.Editor = textBox;
            textBox.TextChanged += (_, _) =>
            {
                block.DraftText = textBox.Text ?? "";
                block.Status = block.IsDirty ? "Unsaved." : "Unchanged.";
                statusTextBlock.Text = EditableBlockStatus(block);
                RefreshEditableViewStatus();
            };

            var border = new Border
            {
                Classes = { "surface" },
                Padding = new Avalonia.Thickness(12),
                Child = new Grid
                {
                    RowDefinitions = new RowDefinitions("Auto,*"),
                    Children =
                    {
                        new Grid
                        {
                            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                            ColumnSpacing = 10,
                            Children =
                            {
                                selectionBox,
                                statusTextBlock,
                                BuildOrderBadge(block, index, total, reorderable)
                            }
                        },
                        textBox
                    }
                }
            };
            Grid.SetRow(textBox, 1);
            editableBlocksPanel.Children.Add(border);

            if (block.SlipId == pendingEditableBlockFocusId)
            {
                pendingEditableBlockFocusId = null;
                Dispatcher.UIThread.Post(() =>
                {
                    textBox.Focus();
                    if (string.Equals(block.BaselineText.Trim(), UntitledSlipText, StringComparison.Ordinal))
                    {
                        textBox.SelectAll();
                    }
                    else
                    {
                        textBox.CaretIndex = textBox.Text?.Length ?? 0;
                    }
                }, DispatcherPriority.Loaded);
            }
        }
    }

    private Control BuildOrderBadge(EditableSlipBlock block, int index, int total, bool reorderable)
    {
        if (!reorderable)
        {
            return new TextBlock
            {
                [Grid.ColumnProperty] = 2,
                Classes = { "muted" },
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Text = $"{index + 1}/{total}",
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var orderBox = new TextBox
        {
            Text = (index + 1).ToString(),
            Width = 52,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = IsOnline && !savingEditableView && block.ConflictText is null,
            [ToolTip.TipProperty] =
                "Type a position and press Enter to reorder this slip within its bucket."
        };
        orderBox.KeyDown += async (_, keyArgs) =>
        {
            if (keyArgs.Key == Key.Enter)
            {
                keyArgs.Handled = true;
                if (int.TryParse(orderBox.Text, out var position))
                {
                    await ReorderEditableBlockAsync(block, position);
                }
                else
                {
                    orderBox.Text = (index + 1).ToString();
                }
            }
            else if (keyArgs.Key == Key.Escape)
            {
                keyArgs.Handled = true;
                orderBox.Text = (index + 1).ToString();
                editableBlocksPanel.Focus();
            }
        };

        return new StackPanel
        {
            [Grid.ColumnProperty] = 2,
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                orderBox,
                new TextBlock
                {
                    Classes = { "muted" },
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = $"/{total}"
                }
            }
        };
    }

    private async Task ReorderEditableBlockAsync(EditableSlipBlock block, int targetOneBased)
    {
        if (!editableViewMode || !IsOnline || currentProject is null || savingEditableView)
        {
            return;
        }

        if (HasDirtyEditableBlocks())
        {
            statusText.Text = "Save All or Cancel before reordering edit blocks.";
            RenderEditableViewBlocks();
            return;
        }

        var total = editableViewBlocks.Count;
        var currentIndex = editableViewBlocks.IndexOf(block);
        if (currentIndex < 0 || total <= 1)
        {
            return;
        }

        var targetIndex = Math.Clamp(targetOneBased - 1, 0, total - 1);
        if (targetIndex == currentIndex)
        {
            RenderEditableViewBlocks();
            return;
        }

        var others = editableViewBlocks.Where(item => item != block).ToList();
        var beforeSlipId = targetIndex < others.Count ? others[targetIndex].SlipId : null;

        savingEditableView = true;
        SetEditingEnabled();
        RefreshEditableViewStatus();
        try
        {
            var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.ReorderSlip,
                new ReorderSlipCommand { BeforeSlipId = beforeSlipId },
                currentProject.Id,
                block.SlipId,
                block.Revision));
            if (response.Status == ZetlResponseStatus.Success)
            {
                statusText.Text = $"Slip moved to position {targetIndex + 1} of {total}.";
            }
            else if (response.Status == ZetlResponseStatus.Conflict)
            {
                var current = response.Conflict?.Current.Deserialize<ZetlSlipSnapshot>(
                    ZetlProtocolJson.Options);
                block.ConflictText = current?.Text;
                statusText.Text = "Reorder conflict; the slip changed elsewhere.";
            }
            else
            {
                statusText.Text = response.Error?.Message ?? $"Reorder failed: {response.Status}.";
            }

            await connection.RefreshAsync();
        }
        finally
        {
            savingEditableView = false;
            SetEditingEnabled();
            RefreshEditableViewStatus();
        }
    }

    private bool EditViewBlocksShareBucket()
    {
        return editableViewBlocks.Count > 0
            && editableViewBlocks
                .Select(block => block.BucketId)
                .Distinct(StringComparer.Ordinal)
                .Count() == 1;
    }

    private string EditableReorderHint()
    {
        if (editableViewBlocks.Count <= 1)
        {
            return "";
        }

        return EditViewBlocksShareBucket()
            ? " Type a slip's number to reorder it within the bucket."
            : " Filter to one bucket to reorder slips by number.";
    }

    private void RefreshEditableViewStatus()
    {
        editViewButton.Content = editableViewMode ? "Read View" : "Edit View";
        editViewMoveBucketBox.IsVisible = editableViewMode;
        // The view picker and its export/copy belong to the read view only.
        viewPickerBox.IsVisible = !editableViewMode;
        copyViewButton.IsVisible = !editableViewMode;
        exportViewButton.IsVisible = !editableViewMode;
        moveSelectedViewButton.IsVisible = editableViewMode;
        saveViewButton.IsVisible = editableViewMode;
        cancelViewButton.IsVisible = editableViewMode;
        saveViewButton.IsEnabled = editableViewMode
            && IsOnline
            && !savingEditableView
            && HasDirtyEditableBlocks();
        moveSelectedViewButton.IsEnabled = editableViewMode
            && IsOnline
            && !savingEditableView
            && !HasDirtyEditableBlocks()
            && editableViewBlocks.Any(block => block.IsSelected)
            && editViewMoveBucketBox.SelectedItem is KastnBucketItem { Id: not null };
        cancelViewButton.IsEnabled = editableViewMode && !savingEditableView;
        editViewButton.IsEnabled = viewerMode && currentProject is not null && !savingEditableView;
        editViewStatusText.Text = editableViewMode
            ? EditableSessionStatus() + EditableReorderHint()
            : "Read-only view. Edit View turns each visible slip into a tracked block.";
    }

    private string EditableSessionStatus()
    {
        var dirty = editableViewBlocks.Count(block => block.IsDirty);
        var conflicts = editableViewBlocks.Count(block => block.ConflictText is not null);
        var selected = editableViewBlocks.Count(block => block.IsSelected);
        return BatchStatus(
            $"{editableViewBlocks.Count} editable block{Plural(editableViewBlocks.Count)}",
            selected > 0 ? $"{selected} selected" : null,
            dirty > 0 ? $"{dirty} unsaved" : "no unsaved changes",
            conflicts > 0 ? $"{conflicts} conflict{Plural(conflicts)}" : null);
    }

    private static string EditableBlockStatus(EditableSlipBlock block)
    {
        var status = string.IsNullOrWhiteSpace(block.Status) ? "Unchanged." : block.Status;
        return $"{block.BucketLabel} | {block.Source} | {block.CapturedAtUtc.LocalDateTime:g} | {status}";
    }

    private bool HasDirtyEditableBlocks()
    {
        return editableViewBlocks.Any(block => block.IsDirty);
    }

    private bool CanLeaveEditableView()
    {
        if (!editableViewMode || !HasDirtyEditableBlocks())
        {
            return true;
        }

        statusText.Text = "Save All or Cancel before leaving edit view.";
        RefreshEditableViewStatus();
        return false;
    }

    private void ExitEditableView(bool clearBlocks)
    {
        editableViewMode = false;
        viewerTextBox.IsVisible = true;
        editViewScroll.IsVisible = false;
        if (clearBlocks)
        {
            editableViewBlocks.Clear();
            editableBlocksPanel.Children.Clear();
        }

        RefreshEditableViewStatus();
    }

    private ZetlSlipSnapshot? SelectedSlip
    {
        get
        {
            var selected = SelectedSlips();
            return selected.Count switch
            {
                0 => currentProject?.Slips.FirstOrDefault(slip => slip.Id == editorState.SlipId),
                1 => selected[0],
                _ => null
            };
        }
    }

    private IReadOnlyList<ZetlSlipSnapshot> SelectedSlips()
    {
        return SelectedSlipItems()
            .Select(item => item.Slip)
            .ToList();
    }

    private IReadOnlyList<SlipListItem> SelectedSlipItems()
    {
        if (slipList.SelectedItems is { Count: > 0 } selectedItems)
        {
            return selectedItems
                .OfType<SlipListItem>()
                .ToList();
        }

        return slipList.SelectedItem is SlipListItem selected
            ? [selected]
            : [];
    }

    private bool IsSlipInDeleted(ZetlSlipSnapshot? slip)
    {
        if (currentProject is null || slip is null)
        {
            return false;
        }

        return KastnWorkbench.IsDeletedBucket(currentProject.Buckets.FirstOrDefault(
            bucket => bucket.Id == slip.BucketId));
    }

    private string SlipMetadata(ZetlSlipSnapshot slip)
    {
        var metadata = $"{slip.Source} | {ShortSession(slip.SessionId)} | "
            + $"{slip.CapturedAtUtc.LocalDateTime:F} | revision {editorState.Revision}";
        if (slip.CaptureOrigin is { } captureOrigin)
        {
            var application = string.IsNullOrWhiteSpace(captureOrigin.ApplicationName)
                ? captureOrigin.ProcessName
                : captureOrigin.ApplicationName;
            var location = string.IsNullOrWhiteSpace(captureOrigin.WindowTitle)
                ? application
                : $"{application} — {captureOrigin.WindowTitle}";
            if (!string.IsNullOrWhiteSpace(location))
            {
                metadata += $" | Captured in {location}";
            }
        }

        if (!IsSlipInDeleted(slip))
        {
            return metadata;
        }

        var origin = currentProject?.Buckets.FirstOrDefault(
            bucket => bucket.Id == slip.DeletedFromBucketId)?.Name ?? "Unknown bucket";
        var deletedAt = slip.DeletedAtUtc is null
            ? "unknown time"
            : slip.DeletedAtUtc.Value.LocalDateTime.ToString("g");
        return $"{metadata} | Deleted from {origin} at {deletedAt}";
    }

    private void RefreshParentBucketHint(ZetlBucketSnapshot? selected)
    {
        if (selected is null || currentProject is null)
        {
            parentBucketHintText.Text = "Choose a bucket to edit its placement.";
            return;
        }

        if (KastnWorkbench.IsDeletedBucket(selected))
        {
            parentBucketHintText.Text = "Deleted is protected and always top level.";
            return;
        }

        if (selected.ParentBucketId is null)
        {
            parentBucketHintText.Text = "Current parent: top level";
            return;
        }

        var parent = currentProject.Buckets.FirstOrDefault(
            bucket => bucket.Id == selected.ParentBucketId);
        parentBucketHintText.Text = parent is null
            ? "Current parent: missing"
            : $"Current parent: {KastnWorkbench.BucketPathLabel(currentProject, parent)}";
    }

    private void UpdateSlipCountText()
    {
        if (currentProject is null)
        {
            slipCountText.Text = "No slips";
            return;
        }

        var selectedCount = SelectedSlipItems().Count;
        slipCountText.Text = selectedCount > 1
            ? $"{slips.Count} of {currentProject.Slips.Count} slips | {selectedCount} selected"
            : $"{slips.Count} of {currentProject.Slips.Count} slips";
    }

    private static string BatchStatus(params string?[] parts)
    {
        var message = string.Join("; ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return message.Length == 0 ? "No slips changed." : $"{message}.";
    }

    private static string Plural(int count)
    {
        return count == 1 ? "" : "s";
    }

    private static bool IsDescendant(
        ZetlProjectSnapshot project,
        string candidateId,
        string ancestorId)
    {
        var current = project.Buckets.FirstOrDefault(bucket => bucket.Id == candidateId);
        while (current?.ParentBucketId is { } parentId)
        {
            if (parentId == ancestorId)
            {
                return true;
            }

            current = project.Buckets.FirstOrDefault(bucket => bucket.Id == parentId);
        }

        return false;
    }

    private static string ShortSession(string? session)
    {
        if (string.IsNullOrWhiteSpace(session))
        {
            return "No session";
        }

        return session.Length <= 12 ? session : session[..12];
    }

    private static string SlipPreviewText(ZetlSlipSnapshot slip)
    {
        var words = slip.Text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(5)
            .ToList();
        return words.Count == 0 ? "Untitled" : string.Join(' ', words);
    }

    private static bool IsUntitledKastnSlip(ZetlSlipSnapshot slip)
    {
        return string.Equals(slip.Source, "kastn", StringComparison.Ordinal)
            && string.Equals(slip.Text.Trim(), UntitledSlipText, StringComparison.Ordinal);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    private sealed record ProjectListItem(
        string Id,
        string Name,
        long MetadataRevision,
        string Detail,
        string PreviewText,
        string ActivityText);

    private sealed record TemplateListItem(
        string Kind,
        string Name,
        string Detail,
        ZetlTemplateDocument Source,
        bool IsUser);

    private sealed record CreationListItem(
        string Kind,
        string Name,
        string Detail,
        ZetlCreationTypeDocument Source,
        bool IsUser);

    // A row in the template editor's bucket list. Label is mutable + observable so
    // renaming a bucket updates the list without rebuilding it (which would steal
    // focus from the name box mid-edit).
    private sealed class TemplateBucketItem : INotifyPropertyChanged
    {
        private string label;

        public TemplateBucketItem(ZetlTemplateBucketDocument bucket, string label)
        {
            Bucket = bucket;
            this.label = label;
        }

        public ZetlTemplateBucketDocument Bucket { get; }

        public string Label
        {
            get => label;
            set
            {
                if (label != value)
                {
                    label = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private sealed record SlipListItem(
        string Id,
        string Text,
        string Detail,
        ZetlSlipSnapshot Slip);
    private sealed record FilterItem(string? Value, string Label);
    private sealed record DateFilterItem(KastnDateFilter Value, string Label);

    private sealed class EditableSlipBlock
    {
        public EditableSlipBlock(ZetlSlipSnapshot slip, string bucketLabel)
        {
            SlipId = slip.Id;
            Revision = slip.Revision;
            BucketId = slip.BucketId;
            BucketLabel = bucketLabel;
            Source = slip.Source;
            CapturedAtUtc = slip.CapturedAtUtc;
            BaselineText = slip.Text;
            DraftText = slip.Text;
            Title = SlipPreviewText(slip);
        }

        public string SlipId { get; }
        public long Revision { get; private set; }
        public string BucketId { get; }
        public string BucketLabel { get; }
        public string Source { get; }
        public DateTimeOffset CapturedAtUtc { get; }
        public string BaselineText { get; private set; }
        public string DraftText { get; set; }
        public string Title { get; private set; }
        public string? Status { get; set; }
        public string? ConflictText { get; set; }
        public TextBlock? StatusText { get; set; }
        public TextBox? Editor { get; set; }
        public bool IsSelected { get; set; }

        public bool IsDirty =>
            !string.Equals(DraftText, BaselineText, StringComparison.Ordinal);

        public void Accept(ZetlSlipSnapshot slip)
        {
            Revision = slip.Revision;
            BaselineText = slip.Text;
            DraftText = slip.Text;
            Title = SlipPreviewText(slip);
            ConflictText = null;
        }
    }
}
