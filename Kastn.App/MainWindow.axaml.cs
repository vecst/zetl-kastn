using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
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

internal partial class MainWindow : Window
{
    private readonly KastnSettings settings;
    private readonly KastnProjectCreationWorkflow projectCreation;
    private string UntitledSlipTitle => settings.Current.UntitledSlipTitle;
    private const int RecentProjectLimit = 10;

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
    private readonly ObservableCollection<CreationListItem> creations = [];
    private LandingSection landingSection;
    // Sentinel for the creation editor's "no view" choice.
    private static readonly ZetlViewDocument NoView = new() { Id = "", Name = "(no view)" };
    private readonly ObservableCollection<LaneCardItem> laneCards = [];
    private readonly ObservableCollection<ProjectListItem> recentProjects = [];
    private readonly ObservableCollection<ProjectListItem> projects = [];
    private readonly ObservableCollection<TemplateListItem> templates = [];
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
    private ZetlProjectSnapshot? currentProject;
    private KastnProjectIndex? projectIndex;
    private KastnProjectIndex ProjectIndex
    {
        get
        {
            var project = currentProject ?? throw new InvalidOperationException("No project selected.");
            if (projectIndex is null || !ReferenceEquals(projectIndex.Project, project))
            {
                projectIndex = new KastnProjectIndex(project);
            }
            return projectIndex;
        }
    }
    internal readonly KastnTreeProjection treeProjection = new();
    private bool refreshing;
    private bool editorUpdating;
    // The logical in-flight-mutation flag every handler consults. The greyed-out
    // look it used to drive immediately is deferred (savingVisual, ~150ms): a
    // fast action never visibly disables the editor and toolbar — the per-action
    // blink — while anything genuinely slow still locks the controls on screen.
    private bool savingCore;
    private bool savingVisual;
    private DispatcherTimer? savingVisualTimer;

    private bool saving
    {
        get => savingCore;
        set
        {
            savingCore = value;
            if (value)
            {
                if (savingVisualTimer is null)
                {
                    savingVisualTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    savingVisualTimer.Tick += (_, _) =>
                    {
                        savingVisualTimer!.Stop();
                        if (!lifetime.IsRetired && savingCore && !savingVisual)
                        {
                            savingVisual = true;
                            SetEditingEnabled();
                        }
                    };
                }

                if (!lifetime.IsRetired) savingVisualTimer.Start();
            }
            else
            {
                savingVisualTimer?.Stop();
                if (savingVisual)
                {
                    savingVisual = false;
                    if (!lifetime.IsRetired) SetEditingEnabled();
                }
            }
        }
    }
    // Set while a batch loops many UpdateSlip commands; OnSnapshotChanged (off-thread)
    // reads it to drop the per-mutation snapshot pushes until the batch's final refresh.
    private volatile bool batching;
    // The single in-flight editor save, so a focus-loss save and a navigation
    // save (e.g. clicking another slip) coalesce instead of racing the `saving`
    // guard.
    private Task<bool>? inflightSave;
    private DispatcherTimer? draftJournalTimer;
    private string? restoredDraftKey;
    private bool recoveredDraftActive;
    private bool addingSlip;
    private bool visibilityUpdating;
    private string? renderServerInstanceId;
    private KastnEditorSaveOperation? pendingEditorSave;
    private KastnEditorMutationAcceptance? pendingEditorMutation;
    private string? pendingBucketSelectionId;
    private string? pendingSlipSelectionId;
    private bool pendingSlipFocus;
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
    private bool landingShowingConsumable;
    private bool landingShowArchived;
    private IReadOnlyList<ZetlProjectSummary> lastProjectSummaries = [];
    private bool suppressLandingProjectSelection;
    private readonly KastnViewRenderCache viewRenderCache = new();
    private readonly KastnWindowLifetime lifetime;

    public MainWindow()
    {
        stateStore = new(log: Console.Error.WriteLine);
        settings = new();
        projectCreation = new(settings);
        InitializeComponent();
        templateCatalog = new(Console.Error.WriteLine);
        creationStore = new(log: Console.Error.WriteLine);
        templateEditor = CreateTemplateEditorPresenter();
        creationEditor = CreateCreationEditorPresenter();
        viewCatalog = new(new ZetlViewStore(log: Console.Error.WriteLine));
        viewPersistence = new(viewCatalog.Store);
        connection = null!;
        editHistory = CreateEditHistory();
        pictureCache = new KastnPictureCache(FetchPictureContentAsync);
        draftStore = new KastnDraftStore(log: Console.Error.WriteLine);
        readerPresenter = new(viewerDocumentPanel, viewerDocumentScroll, pictureCache);
        boardPresenter = new(boardColumnsPanel, boardScrollViewer, pictureCache);
        viewEditor = CreateViewEditorPresenter();
        lifetime = CreateWindowLifetime();
        WireWindowLifetime();
    }

    public MainWindow(
        KastnConnectionController connection,
        KastnDraftStore? draftStore = null,
        ZetlViewStore? viewStore = null,
        KastnSettings? settings = null,
        ZetlTemplateStore? templateStore = null,
        ZetlCreationTypeStore? creationStore = null,
        KastnStateStore? stateStore = null)
    {
        this.stateStore = stateStore ?? new(log: Console.Error.WriteLine);
        this.settings = settings ?? new();
        projectCreation = new(this.settings);
        this.connection = connection;
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
        viewEditor = CreateViewEditorPresenter();
        lifetime = CreateWindowLifetime();
        Icon = KastnIcon.Create();
        landingLaneItems.ItemsSource = laneCards;
        landingProjectList.ItemsSource = recentProjects;
        landingProjectWorkspaceList.ItemsSource = projects;
        landingTemplateItems.ItemsSource = templates;
        sourceFilterBox.ItemsSource = sources;
        sessionFilterBox.ItemsSource = sessions;
        dateFilterBox.ItemsSource = dates;
        parentBucketBox.ItemsSource = parentBuckets;
        moveBucketBox.ItemsSource = moveBuckets;
        bucketRenderKindBox.ItemsSource = bucketRenderKinds;
        fontFamilyBox.ItemsSource = FontFamilyChoices;
        fontSizeBox.ItemsSource = FontSizeChoices;
        textColorBox.ItemsSource = TextColorChoices;

        dates.Add(new DateFilterItem(KastnDateFilter.All, "All time"));
        dates.Add(new DateFilterItem(KastnDateFilter.Today, "Today"));
        dates.Add(new DateFilterItem(KastnDateFilter.Last7Days, "Last 7 days"));
        dates.Add(new DateFilterItem(KastnDateFilter.Last30Days, "Last 30 days"));
        dateFilterBox.SelectedIndex = 0;
        typeFilterBox.ItemsSource = new[]
        {
            new TypeFilterItem(null, "All types"),
            new TypeFilterItem(ZetlSlipType.Text, "Text"),
            new TypeFilterItem(ZetlSlipType.Url, "Links"),
            new TypeFilterItem(ZetlSlipType.Picture, "Pictures")
        };
        typeFilterBox.SelectedIndex = 0;
        RebuildTemplateCards();

        connection.SnapshotChanged += OnSnapshotChanged;
        landingProjectList.SelectionChanged += OnProjectSelectionChanged;
        landingProjectWorkspaceList.SelectionChanged += OnProjectSelectionChanged;
        projectTree.SelectionChanged += OnTreeSelectionChanged;
        SetupTreeDragDrop();
        detailEditorButton.Click += (_, _) => SetDetailPaneMode(showDetails: false);
        detailDetailsButton.Click += (_, _) => SetDetailPaneMode(showDetails: true);
        viewDeletedButton.IsCheckedChanged += (_, _) => OnViewDeletedToggled();
        bucketHeadingSizeBox.ItemsSource = new[] { "Normal size", "Large", "Small" };
        bucketHeadingAlignBox.ItemsSource = new[] { "Left", "Center", "Right" };
        bucketHeadingSizeBox.SelectionChanged += async (_, _) => await OnBucketHeadingChangedAsync();
        bucketHeadingAlignBox.SelectionChanged += async (_, _) => await OnBucketHeadingChangedAsync();
        bucketHeadingBoldCheck.IsCheckedChanged += async (_, _) => await OnBucketHeadingChangedAsync();
        searchBox.TextChanged += (_, _) => RefreshSlipView();
        sourceFilterBox.SelectionChanged += (_, _) => RefreshSlipView();
        sessionFilterBox.SelectionChanged += (_, _) => RefreshSlipView();
        dateFilterBox.SelectionChanged += (_, _) => RefreshSlipView();
        typeFilterBox.SelectionChanged += (_, _) => RefreshSlipView();
        slipEditor.TextChanged += (_, _) => OnEditorTextChanged();
        // Save when the editor loses focus rather than on a keystroke timer, so
        // typing is never interrupted by a mid-edit save + refresh. The user can turn
        // this off in Settings; switching slips and explicit Save still commit edits.
        slipEditor.LostFocus += async (_, _) =>
        {
            if (this.settings.Current.KastnAutosave)
            {
                await SaveEditorAsync();
            }
        };
        // Tunnel so Ctrl+Enter saves before the editor's AcceptsReturn turns it into a
        // newline; the explicit save keeps the caret so typing can continue.
        slipEditor.AddHandler(
            InputElement.KeyDownEvent,
            OnSlipEditorPreviewKeyDown,
            RoutingStrategies.Tunnel);
        slipEditor.KeyUp += OnSlipEditorKeyUp;
        slipEditor.PointerReleased += OnSlipEditorPointerReleased;
        // The editor has no undo stack of its own: every action — typing included —
        // undoes through Kastn's single ordered history, so Ctrl+Z walks all
        // interactions in the order they happened. Pending typing enters that
        // history via the save flush at the start of each undo/redo.
        slipEditor.IsUndoEnabled = false;

        refreshMenuItem.Click += async (_, _) => await RefreshAsync();
        journalModeMenuItem.Click += async (_, _) => await ToggleJournalModeAsync();
        closeProjectMenuItem.Click += async (_, _) => await CloseProjectAsync();
        deleteProjectMenuItem.Click += async (_, _) => await DeleteProjectAsync();
        exitMenuItem.Click += (_, _) => Close();
        newSlipMenuItem.Click += async (_, _) => await AddSlipAsync();
        saveSlipMenuItem.Click += async (_, _) => await SaveEditorAsync();
        deleteSlipMenuItem.Click += async (_, _) => await DeleteSlipAsync();
        focusSearchMenuItem.Click += (_, _) => searchBox.Focus();
        focusProjectsMenuItem.Click += (_, _) => landingProjectsPanel.Focus();
        focusBucketsMenuItem.Click += (_, _) => projectTree.Focus();
        focusSlipsMenuItem.Click += (_, _) => FocusMainView();
        aboutMenuItem.Click += ShowAbout;
        addBucketButton.Click += async (_, _) => await AddBucketAsync();
        saveBucketButton.Click += async (_, _) => await SaveBucketAsync();
        deleteBucketButton.Click += async (_, _) => await DeleteBucketAsync();
        closeProjectButton.Click += async (_, _) => await CloseProjectAsync();
        newSlipButton.Click += async (_, _) => await AddSlipAsync();
        viewModeListButton.Click += (_, _) => SetBoardMode(false);
        viewModeBoardButton.Click += (_, _) => SetBoardMode(true);
        boardModeMenuItem.Click += (_, _) => SetBoardMode(boardModeMenuItem.IsChecked);
        // Alignment, bold/italic/strike, and the list markers are whole-slip render
        // properties: a single selected slip toggles its own, a multi-slip / bucket
        // selection applies the change to every selected slip at once (batch).
        // Inline emphasis within the text is typed Markdown; only the code and link
        // buttons still set style ranges over the editor selection.
        alignLeftButton.Click += async (_, _) => await AlignSlipsAsync("left");
        alignCenterButton.Click += async (_, _) => await AlignSlipsAsync("center");
        alignRightButton.Click += async (_, _) => await AlignSlipsAsync("right");
        fontFamilyBox.SelectionChanged += async (_, _) => await OnFontFamilyChangedAsync();
        fontSizeBox.SelectionChanged += async (_, _) => await OnFontSizeChangedAsync();
        textColorBox.SelectionChanged += async (_, _) => await OnTextColorChangedAsync();
        boldButton.Click += async (_, _) => await ToggleSlipStyleAsync(ZetlInlineStyleKinds.Bold);
        italicButton.Click += async (_, _) => await ToggleSlipStyleAsync(ZetlInlineStyleKinds.Italic);
        strikeButton.Click += async (_, _) => await ToggleSlipStyleAsync(ZetlInlineStyleKinds.Strike);
        codeButton.Click += async (_, _) => await ToggleInlineStyleAsync(ZetlInlineStyleKinds.Code);
        linkButton.Click += async (_, _) => await SetEditorWebLinkAsync();
        wikiLinkButton.Click += async (_, _) => await InsertSlipLinkAsync();
        ignoreBucketRenderKindCheck.IsCheckedChanged += async (_, _) => await OnIgnoreBucketRenderKindChangedAsync();
        representationToggleButton.Click += async (_, _) => await ToggleSlipRepresentationAsync();
        attachPictureButton.Click += async (_, _) => await AttachSlipPictureAsync();
        removePictureButton.Click += async (_, _) => await RemoveSlipPictureAsync();
        bulletListButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Bullet);
        numberListButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Ordered);
        taskListButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Task);
        headingButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Heading);
        quoteButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Quote);
        codeBlockButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Code);
        insertDividerButton.Click += async (_, _) => await InsertDividerSlipAsync();
        insertGroupButton.Click += async (_, _) => await InsertGroupBucketAsync();
        RefreshViewCatalog(null);
        viewPickerBox.SelectionChanged += (_, _) =>
        {
            if (!refreshing)
            {
                RefreshViewer();
            }
        };
        copyViewButton.Click += async (_, _) => await CopyRenderedViewAsync();
        exportViewButton.Click += async (_, _) => await ExportRenderedViewAsync();
        saveViewSettingsButton.Click += async (_, _) => await SaveViewAsync();
        cancelViewSettingsButton.Click += async (_, _) => await CancelViewEditAsync();
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
        landingProjectsButton.Click += (_, _) => ShowLandingSection(LandingSection.Projects);
        landingTemplatesButton.Click += (_, _) => ShowLandingSection(LandingSection.Templates);
        landingCreateButton.Click += (_, _) => ShowLandingSection(LandingSection.Creations);
        landingCurrentProjectsButton.Click += (_, _) => SetArchivedProjectMode(showArchived: false);
        landingArchivedProjectsButton.Click += (_, _) => SetArchivedProjectMode(showArchived: true);
        landingCaptureButton.Click += (_, _) => SetTemplateType(consumable: false);
        landingConsumableButton.Click += (_, _) => SetTemplateType(consumable: true);
        landingCreationItems.ItemsSource = creations;
        WireTemplateEditor();
        WireCreationEditor();
        SizeChanged += (_, _) => RefreshLandingGridLayout();
        KeyDown += OnKeyDown;
        WireWindowLifetime();
        ApplySnapshot(connection.Current);
    }

    public async Task ActivateRequestAsync(string? projectId)
    {
        if (!lifetime.CanActivate) return;
        lifetime.Activate();
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            await connection.NavigateToProjectAsync(projectId);
        }
    }

    internal void ReportActivationFailure(Exception exception)
    {
        if (lifetime.IsRetired) return;
        statusText.Text = $"Kastn could not open the requested project. {exception.Message}";
    }

    private void OnSnapshotChanged(object? sender, KastnSessionSnapshot snapshot)
    {
        // During a batch (e.g. aligning or numbering many slips) the service publishes
        // a snapshot per mutation; applying each would rebuild the tree and View N
        // times in sequence. Drop the intermediate pushes — the batch does one
        // RefreshAsync at the end.
        if (batching || lifetime.IsRetired)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(KastnSessionSnapshot snapshot)
    {
        if (lifetime.IsRetired) return;
        var priorProjectId = currentProject?.Id;
        var selectedProjectId = snapshot.Project?.Id;
        var selectedBucketId = SelectedBucketId;
        var selectedSlipId = pendingSlipSelectionId ?? editorState.SlipId;

        // Rendering has its own server lifetime: history reconciliation can
        // observe a restart before its queued UI snapshot is applied.
        var serverChanged = snapshot.ConnectionState == KastnConnectionState.Online
            && renderServerInstanceId is not null && snapshot.ServerInstanceId is not null
            && renderServerInstanceId != snapshot.ServerInstanceId;
        editHistory.ObserveSession(snapshot);
        if (snapshot.ConnectionState == KastnConnectionState.Online && snapshot.ServerInstanceId is not null)
            renderServerInstanceId = snapshot.ServerInstanceId;

        // Rendered controls and decoded pictures belong to this project/server.
        if (!string.Equals(priorProjectId, selectedProjectId, StringComparison.Ordinal) || serverChanged)
        {
            readerPresenter.Clear();
            boardPresenter.Clear();
            viewRenderCache.Clear();
            pictureCache.Reset();
        }

        refreshing = true;
        try
        {
            lastProjectSummaries = snapshot.Projects;
            PopulateProjectCards();
            RefreshLandingGridLayout();

            landingProjectList.SelectedItem = null;
            landingProjectWorkspaceList.SelectedItem = null;

            currentProject = snapshot.Project;
            if (currentProject is { } projectSnapshot)
            {
                var selectedViewId = string.Equals(priorProjectId, projectSnapshot.Id, StringComparison.Ordinal)
                    ? (viewPickerBox.SelectedItem as ZetlViewDocument)?.Id
                    : projectSnapshot.DefaultViewId;
                RefreshViewCatalog(projectSnapshot, selectedViewId, force: serverChanged);
                if (!string.Equals(priorProjectId, projectSnapshot.Id, StringComparison.Ordinal))
                {
                    // Remember the opened project for the "reopen last project" startup
                    // preference (Kastn-owned state, not the shared settings file).
                    stateStore.LastProjectId = projectSnapshot.Id;
                    selectedBucketId = null;
                    selectedSlipId = null;
                    editorState.Select(null);
                    if (RecoverySlipId(projectSnapshot) is { } recoverySlipId)
                    {
                        selectedSlipId = recoverySlipId;
                        pendingSlipSelectionId = recoverySlipId;
                    }
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
                var acknowledged = pendingEditorSave is { } save && save.ProjectId == projectSnapshot.Id
                    && save.TryAcknowledgeSnapshot(currentSlip)
                    || pendingEditorMutation is { } mutation && mutation.Command.ProjectId == projectSnapshot.Id
                        && mutation.TryAcknowledgeSnapshot(currentSlip, projectSnapshot);
                if (!acknowledged)
                {
                    editorState.Reconcile(currentSlip);
                }
                UpdateEditorFromState();
                RefreshSlipView(force: true);
                RestoreDraftIfAvailable(projectSnapshot);
                projectView.IsVisible = true;
                emptyState.IsVisible = false;
            }
            else
            {
                currentProject = null;
                projectIndex = null;
                RefreshViewCatalog(null, force: false);
                treeProjection.Clear();
                projectTree.SetHierarchy(treeProjection.Roots);
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

            // Local authoring sessions stay open across snapshots. Structured
            // view writes remain scoped to the project where that session opened.
            if (templateEditor.State is not null)
            {
                projectView.IsVisible = false;
                emptyState.IsVisible = false;
                viewEditorView.IsVisible = false;
                templateEditorView.IsVisible = true;
            }
            else if (viewEditor.State is not null)
            {
                projectView.IsVisible = false;
                emptyState.IsVisible = false;
                templateEditorView.IsVisible = false;
                viewEditorView.IsVisible = true;
            }
            else if (creationEditor.State is not null)
            {
                projectView.IsVisible = false;
                emptyState.IsVisible = false;
                templateEditorView.IsVisible = false;
                viewEditorView.IsVisible = false;
                creationEditorView.IsVisible = true;
            }

            viewEditor.RefreshPreview();
            SetConnectionState(snapshot);
        }
        finally
        {
            refreshing = false;
        }
    }

    private void ShowLandingSection(LandingSection section)
    {
        landingSection = section;
        landingProjectList.SelectedItem = null;
        landingProjectWorkspaceList.SelectedItem = null;
        // Re-read each catalog from disk on visit so added/removed user files show
        // up without restarting Kastn.
        if (section == LandingSection.Templates)
        {
            RebuildTemplateCards();
        }

        if (section == LandingSection.Creations)
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
        var loadedTemplates = templateCatalog.LoadAll();
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
        var showProjects = landingSection == LandingSection.Projects;
        var showTemplates = landingSection == LandingSection.Templates;
        var showCreations = landingSection == LandingSection.Creations;
        // The toggle stays visible even with no visible projects, so archived-only
        // workspaces can still reveal their projects.
        landingProjectsPanel.IsVisible = showChoices;
        landingLowerContent.IsVisible = showChoices;
        landingProjectLibraryHeader.IsVisible = showChoices;
        landingProjectList.IsVisible = showChoices && recentProjects.Count > 0;
        landingProjectWorkspaceList.IsVisible = showChoices && showProjects && projects.Count > 0;
        landingTemplateList.IsVisible = showChoices && showTemplates;
        landingTemplateList.IsEnabled = IsOnline;
        landingCreationList.IsVisible = showChoices && showCreations;
        landingCreationList.IsEnabled = IsOnline;
        landingLowerActionRow.IsVisible = showChoices
            && (showProjects || showTemplates || showCreations);
        landingProjectArchiveToggle.IsVisible = showChoices && showProjects;
        landingTemplateTypeToggle.IsVisible = showChoices && showTemplates;
        landingTemplateTypeToggle.IsEnabled = IsOnline;
        landingNewTemplateButton.IsVisible = showChoices && showTemplates;
        landingNewCreationButton.IsVisible = showChoices && showCreations;
        landingWorkspaceTitle.Text = showCreations
            ? "Create"
            : showTemplates ? "Templates" : "Projects";
        landingWorkspaceSubtitle.Text = showCreations
            ? "Start from a saved creation type."
            : showTemplates
                ? "Start a project from a reusable template."
                : "Open a project or manage the library.";
        landingProjectsButton.IsEnabled = !showProjects;
        landingTemplatesButton.IsEnabled = !showTemplates;
        landingCurrentProjectsButton.IsEnabled = landingShowArchived;
        landingArchivedProjectsButton.IsEnabled = !landingShowArchived;
        landingCreateButton.IsVisible = showChoices;
        landingCreateButton.IsEnabled = !showCreations;
        landingCaptureButton.IsEnabled = landingShowingConsumable;
        landingConsumableButton.IsEnabled = !landingShowingConsumable;
        RefreshLandingGridLayout();
    }

    private void RefreshLandingGridLayout()
    {
        var contentWidth = Math.Max(560, Bounds.Width - 500);
        var lowerHeight = Math.Max(240, Bounds.Height - 210);
        landingProjectList.MaxHeight = lowerHeight;
        landingProjectWorkspaceList.Width = contentWidth;
        landingProjectWorkspaceList.MaxHeight = lowerHeight;
        landingTemplateList.Width = contentWidth;
        landingTemplateList.MaxHeight = lowerHeight;
        landingCreationList.Width = contentWidth;
        landingCreationList.MaxHeight = lowerHeight;
    }

    // Rebuild the landing project cards from the last snapshot's summaries.
    // Recents stay on current projects; the workspace tab switches current/archived.
    // Callers manage the `refreshing` guard
    // because mutating `projects` fires the list's selection handler.
    private void PopulateProjectCards()
    {
        laneCards.Clear();
        recentProjects.Clear();
        projects.Clear();
        var allProjects = lastProjectSummaries.Select(CreateProjectListItem).ToList();

        AddLaneCard(allProjects, ZetlStateRules.NormalLane, LaneLabel(ZetlStateRules.NormalLane));
        AddLaneCard(allProjects, ZetlStateRules.ShiftLane, LaneLabel(ZetlStateRules.ShiftLane));

        var laneProjectIds = laneCards
            .SelectMany(card => new[] { card.Project?.Id, card.OverlayProject?.Id })
            .Where(id => id is not null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var project in allProjects
            .Where(project =>
                !project.IsTemporary
                && !laneProjectIds.Contains(project.Id)
                && project.IsArchived == landingShowArchived)
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase))
        {
            projects.Add(project);
        }

        foreach (var project in allProjects
            .Where(project => !project.IsTemporary && !project.IsArchived)
            .OrderByDescending(project => project.LastActivityUtc ?? DateTimeOffset.MinValue)
            .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .Take(RecentProjectLimit))
        {
            recentProjects.Add(project);
        }
    }

    private static ProjectListItem CreateProjectListItem(ZetlProjectSummary project)
    {
        return new ProjectListItem(
            project.Id,
            project.Name,
            project.MetadataRevision,
            LandingProjectDetail(project),
            string.IsNullOrWhiteSpace(project.PreviewText)
                ? "No slips yet"
                : project.PreviewText,
            LandingProjectActivity(project),
            project.LastActivityUtc,
            project.Status,
            project.VisibleSlipCount,
            project.ActiveLane,
            project.UnderlyingLane,
            project.CanCreateTemporaryFromReplay,
            string.Equals(
                project.Kind,
                ZetlStateRules.TemporaryConsumableProjectKind,
                StringComparison.Ordinal));
    }

    private void AddLaneCard(
        IReadOnlyList<ProjectListItem> visibleProjects,
        string lane,
        string label)
    {
        var overlay = visibleProjects.FirstOrDefault(project =>
            project.IsTemporary
            && string.Equals(project.ActiveLane, lane, StringComparison.Ordinal));
        var project = visibleProjects.FirstOrDefault(project =>
            !project.IsTemporary
            && (string.Equals(project.ActiveLane, lane, StringComparison.Ordinal)
                || (overlay is not null
                    && string.Equals(project.UnderlyingLane, lane, StringComparison.Ordinal))));
        laneCards.Add(new LaneCardItem(lane, label, project, overlay));
    }

    private void SetArchivedProjectMode(bool showArchived)
    {
        if (landingShowArchived == showArchived)
        {
            return;
        }

        landingShowArchived = showArchived;
        refreshing = true;
        try
        {
            PopulateProjectCards();
        }
        finally
        {
            refreshing = false;
        }

        RefreshLandingGridLayout();
        RefreshLandingMode();
    }

    private static string LandingProjectDetail(ZetlProjectSummary project)
    {
        var detail = $"{project.VisibleSlipCount} slip{Plural(project.VisibleSlipCount)}"
            + $" | {project.VisibleBucketCount} bucket{Plural(project.VisibleBucketCount)}";
        if (string.Equals(project.Kind, ZetlStateRules.TemporaryConsumableProjectKind, StringComparison.Ordinal))
        {
            detail = $"Temporary | {detail}";
        }

        return project.DeletedSlipCount == 0
            ? detail
            : $"{detail} | {project.DeletedSlipCount} deleted";
    }

    private static string LandingProjectActivity(ZetlProjectSummary project)
    {
        return project.LastActivityUtc is null
            ? "No activity yet"
            : $"Last slip {project.LastActivityUtc.Value.LocalDateTime:g}";
    }

    private string LaneLabel(string lane) =>
        this.settings.Current.LaneLabel(string.Equals(lane, ZetlStateRules.ShiftLane, StringComparison.Ordinal));

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

    private void RefreshBuckets(ZetlProjectSnapshot project, string? selectedBucketId)
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
        treeProjection.Update(ProjectIndex, showingDeleted, this.settings.Current.MaxSlipLabelLength);
        projectTree.SetHierarchy(treeProjection.Roots);
        projectTree.RestoreAnchor(treeAnchor);

        UpdateDeletedToggle(project);

        // A pending override (after creating/moving a slip) wins; else restore the
        // remembered group; else fall back to the requested bucket / first slip /
        // first bucket so the editor and tree agree on open.
        IReadOnlyList<string> restoreIds;
        if (pendingSlipSelectionId is { } pending && treeProjection.Find(pending) is not null)
        {
            restoreIds = [pending];
        }
        else
        {
            var present = rememberedIds.Where(id => treeProjection.Find(id) is not null).ToList();
            restoreIds = present.Count > 0
                ? present
                : (selectedBucketId
                    ?? treeProjection.FirstSlip?.Id
                    ?? project.Buckets.FirstOrDefault()?.Id) is { } fallback
                        ? [fallback]
                        : [];
        }

        ApplyTreeNodeSelection(restoreIds);
        RefreshBucketEditor();
        RefreshDestinationBuckets();
        SetDetailPaneMode(detailShowingMetadata);
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
        if (!IsOnline || saving || currentProject is null)
        {
            return;
        }

        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.SetBucketHeading,
            new SetBucketHeadingCommand { Align = align, Bold = bold, Level = level },
            currentProject.Id,
            bucket.Id,
            bucket.Revision));
        if (response.Status == ZetlResponseStatus.Success)
        {
            await connection.SynchronizeAsync();
        }
        else if (response.Status != ZetlResponseStatus.Conflict)
        {
            statusText.Text = response.Error?.Message ?? "Heading update failed.";
        }
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

        UpdateFilterButton();
        var selectedId = pendingSlipSelectionId ?? editorState.SlipId;
        var filtered = CurrentFilteredSlips();

        var wasRefreshing = refreshing;
        refreshing = true;
        try
        {
            if (pendingTreeSelectionSave is { } selectionSave && selectionSave.ProjectId == currentProject.Id
                && selectionSave.Generation == editHistory.Generation && selectionSave.EditorVersion == editorState.SelectionVersion)
            {
                // Keep the saved editor session until the selection handler can
                // accept its clicked destination or restore it on save failure.
                RefreshViewer();
                RefreshDestinationBuckets();
                return;
            }
            // Bind the editor from the explicit selection: a batch clears it; a
            // pending/just-created or surviving single slip loads it; otherwise (and
            // not in title mode) default to the first slip.
            var selected = filtered.FirstOrDefault(slip => slip.Id == selectedId);
            if (pendingSlipSelectionId is null
                && CurrentSelection() is KastnSelection.Slips { SlipIds.Count: > 1 })
            {
                editorState.Select(null);
                UpdateEditorFromState();
            }
            else if (selected is not null)
            {
                if (pendingSlipSelectionId == selected.Id)
                {
                    pendingSlipSelectionId = null;
                    editorState.Select(selected);
                    UpdateEditorFromState();
                    if (pendingSlipFocus)
                    {
                        pendingSlipFocus = false;
                        slipEditor.Focus();
                        if (IsUntitledKastnSlip(selected))
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
            else if (!editorState.IsDirty && editorState.ConflictCurrent is null && TitleModeBucket() is null)
            {
                editorState.Select(filtered.FirstOrDefault());
                UpdateEditorFromState();
            }

            RefreshViewer();
            RefreshDestinationBuckets();
        }
        finally
        {
            refreshing = wasRefreshing;
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

        filtersButton.Content = activeCount == 0 ? "Filters ▾" : $"Filters ({activeCount}) ▾";
    }

    private async void OnProjectSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (suppressLandingProjectSelection)
        {
            suppressLandingProjectSelection = false;
            refreshing = true;
            landingProjectList.SelectedItem = null;
            landingProjectWorkspaceList.SelectedItem = null;
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
        if (ProjectFromControl(sender) is { } project)
        {
            await OpenProjectCardAsync(project);
        }
    }

    private async void OnProjectCardRenameClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (ProjectFromControl(sender) is { } project)
        {
            await RenameProjectAsync(project);
        }
    }

    private async void OnProjectCardPinClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (ProjectFromControl(sender) is { } project)
        {
            await SetActiveProjectAsync(project, shifted: false);
        }
    }

    private async void OnProjectCardSetAlternateClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (ProjectFromControl(sender) is { } project)
        {
            await SetActiveProjectAsync(project, shifted: true);
        }
    }

    private async void OnProjectCardDeleteClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (ProjectFromControl(sender) is { } project)
        {
            await DeleteProjectAsync(project);
        }
    }

    private async void OnProjectCardStatusClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (ProjectFromControl(sender) is { } project)
        {
            await SetProjectStatusAsync(project, project.StatusActionTarget);
        }
    }

    private async void OnProjectCardUseTemporarilyClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (ProjectFromControl(sender) is { } project)
        {
            await CreateTemporaryProjectFromReplayAsync(project);
        }
    }

    private static ProjectListItem? ProjectFromControl(object? sender)
    {
        return sender is Control control
            ? control.Tag as ProjectListItem ?? control.DataContext as ProjectListItem
            : null;
    }

    private async Task OpenProjectCardAsync(ProjectListItem project)
    {
        if (!await SaveEditorAsync())
        {
            refreshing = true;
            landingProjectList.SelectedItem = null;
            landingProjectWorkspaceList.SelectedItem = null;
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

        if (!await SaveEditorAsync())
        {
            statusText.Text = "Save or resolve the current slip before closing the project.";
            return;
        }

        editorState.Select(null);
        UpdateEditorFromState();
        pendingBucketSelectionId = null;
        pendingSlipSelectionId = null;
        await connection.NavigateToProjectAsync(null);
    }

    private void SetConnectionState(KastnSessionSnapshot snapshot)
    {
        var creationStatus = CreationStatus(snapshot);
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
    }

    private async void OnSlipEditorPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.Enter or Key.S)
        {
            e.Handled = true;
            await SaveEditorKeepingFocusAsync();
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

    // Save the current slip without losing the editor: a save can trigger a snapshot
    // refresh that re-selects the tree and steals focus, so restore focus and the
    // caret afterward so the user can keep typing.
    private async Task SaveEditorKeepingFocusAsync()
    {
        var hadFocus = slipEditor.IsFocused;
        var caret = slipEditor.CaretIndex;
        await SaveEditorAsync();
        if (!hadFocus)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (lifetime.IsRetired || editorState.SlipId is null)
                {
                    return;
                }

                slipEditor.Focus();
                slipEditor.CaretIndex = Math.Min(caret, slipEditor.Text?.Length ?? 0);
            },
            DispatcherPriority.Background);
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
        else if (args.KeyModifiers.HasFlag(KeyModifiers.Control)
            && args.Key is Key.S or Key.Enter)
        {
            // Ctrl+S or Ctrl+Enter saves the current slip (Enter alone inserts a
            // newline in the editor). The editor's own preview handler catches these
            // when it is focused; this covers a save from elsewhere.
            args.Handled = true;
            await SaveEditorKeepingFocusAsync();
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

    // The landing page shows exactly one of these lists at a time.
    private enum LandingSection
    {
        Projects,
        Templates,
        Creations
    }

    private sealed record ProjectListItem(
        string Id,
        string Name,
        long MetadataRevision,
        string Detail,
        string PreviewText,
        string ActivityText,
        DateTimeOffset? LastActivityUtc,
        string Status,
        int VisibleSlipCount,
        string ActiveLane,
        string UnderlyingLane,
        bool CanCreateTemporaryFromReplay,
        bool IsTemporary)
    {
        public bool IsActive =>
            string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase);

        public bool IsArchived =>
            string.Equals(Status, "Archived", StringComparison.OrdinalIgnoreCase);

        // The badge only appears for non-Active projects, so Active shows nothing.
        public bool ShowStatusBadge => !IsActive;

        // One contextual status action per card: seal/put-away an active project,
        // or bring a finished/archived one back.
        public string StatusActionLabel => IsActive
            ? "Archive"
            : IsArchived ? "Unarchive" : "Reactivate";

        public string StatusActionTarget => IsActive ? "Archived" : "Active";

        public bool CanUseTemporarily => IsArchived && CanCreateTemporaryFromReplay;

        public bool CanSetActive =>
            IsActive && !string.Equals(ActiveLane, ZetlStateRules.NormalLane, StringComparison.Ordinal);

        public bool CanSetAlternateActive =>
            IsActive && !string.Equals(ActiveLane, ZetlStateRules.ShiftLane, StringComparison.Ordinal);

        public string SetActiveActionLabel =>
            string.Equals(ActiveLane, ZetlStateRules.NormalLane, StringComparison.Ordinal)
                ? "Active"
                : "Set Active";
    }

    private sealed record LaneCardItem(
        string Lane,
        string Label,
        ProjectListItem? Project,
        ProjectListItem? OverlayProject)
    {
        public bool HasProject => Project is not null;
        public bool HasOverlay => OverlayProject is not null;
        public bool IsEmpty => Project is null;
        public string ProjectName => Project?.Name ?? $"No {Label} project";
        public string ProjectDetail => Project?.Detail ?? "Select or create a project to keep here.";
        public string ProjectPreview => Project?.PreviewText ?? "This lane is empty.";
        public string ProjectActivity => Project?.ActivityText ?? "";
        public string EmptyTitle => $"{Label} is empty";
        public string EmptyDetail => "Choose a project from the library to keep this lane ready.";
        public string OverlayName => OverlayProject?.Name ?? "";
        public string OverlayDetail => OverlayProject?.Detail ?? "";
        public string OverlayProgress => OverlayProject is null
            ? ""
            : $"{OverlayProject.VisibleSlipCount} replay item{Plural(OverlayProject.VisibleSlipCount)} left";
    }

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

    private sealed record FilterItem(string? Value, string Label);
    private sealed record TypeFilterItem(ZetlSlipType? Value, string Label);
    private sealed record DateFilterItem(KastnDateFilter Value, string Label);

    private void SetBoardMode(bool active)
    {
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
