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
    private string UntitledSlipTitle => CurrentAppSettings().UntitledSlipTitle;
    private const int RecentProjectLimit = 10;
    private const int LaneLabelMaxLength = 20;
    private const long MaximumPictureCacheBytes = 128L * 1024 * 1024;

    private readonly KastnConnectionController connection;
    // Built-ins plus user templates; corrupt/invalid user files are skipped with a
    // diagnostic written to Console.Error (Kastn's existing diagnostic channel).
    private readonly KastnTemplateCatalog templateCatalog = new(Console.Error.WriteLine);
    private IReadOnlyList<ZetlTemplateDocument> loadedTemplates = [];
    // Built-in plus user views for the read-view renderer.
    private readonly ZetlViewStore viewStore = new(log: Console.Error.WriteLine);
    private IReadOnlyList<ZetlViewDocument> globalViews = ZetlViewDefaults.CreateAll();
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
    private bool editingProjectScopedView;
    private bool viewEditorUpdating;
    private string viewBaselineJson = "";
    // Kastn-owned runtime state (last project opened) for the startup preference.
    private readonly KastnStateStore stateStore = new(log: Console.Error.WriteLine);
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
    private readonly ObservableCollection<LaneCardItem> laneCards = [];
    private readonly ObservableCollection<ProjectListItem> recentProjects = [];
    private readonly ObservableCollection<ProjectListItem> projects = [];
    private readonly ObservableCollection<TemplateListItem> templates = [];
    private readonly ObservableCollection<SlipListItem> slips = [];
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
    internal readonly KastnEditorState editorState = new();
    private readonly Dictionary<string, ZetlPictureContent> pictureCache = new(StringComparer.Ordinal);
    private readonly Queue<string> pictureCacheOrder = [];
    private readonly Dictionary<string, Task<ZetlPictureContent?>> pictureLoads = new(StringComparer.Ordinal);
    // Decoded picture bitmaps by content hash and decode width. Pictures are
    // content-addressed, so an entry never goes stale; the cache clears on project
    // switch (and window close). Owning decoded bitmaps here lets view and board
    // rebuilds reuse them synchronously instead of re-decoding every picture on
    // every action — the main cost behind the editor sitting disabled with the
    // view flashing for a second per mutation.
    private readonly Dictionary<(string Sha, int Width), Bitmap> decodedPictureCache = new();
    private ZetlProjectSnapshot? currentProject;
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
                        if (savingCore && !savingVisual)
                        {
                            savingVisual = true;
                            SetEditingEnabled();
                        }
                    };
                }

                savingVisualTimer.Start();
            }
            else
            {
                savingVisualTimer?.Stop();
                if (savingVisual)
                {
                    savingVisual = false;
                    SetEditingEnabled();
                }
            }
        }
    }
    // Set while a batch loops many UpdateSlip commands; OnSnapshotChanged (off-thread)
    // reads it to drop the per-mutation snapshot pushes until the batch's final refresh.
    private volatile bool batching;
    private string lastViewCatalogSignature = "";
    // The single in-flight editor save, so a focus-loss save and a navigation
    // save (e.g. clicking another slip) coalesce instead of racing the `saving`
    // guard.
    private Task<bool>? inflightSave;
    private bool addingSlip;
    private bool visibilityUpdating;
    private string? pendingSaveText;
    private string? pendingBucketSelectionId;
    private string? pendingSlipSelectionId;
    private bool pendingSlipFocus;
    private bool detailShowingMetadata;
    private bool slipRenderOptionUpdating;
    // When true the project tree shows only the Deleted bucket's slips (browse +
    // restore), instead of the normal working tree.
    private bool showingDeleted;
    private bool boardModeActive;
    private readonly Dictionary<string, Border> boardSlipCards = new(StringComparer.Ordinal);
    private string? highlightedBoardSlipId;
    private GridLength treeColumnWidth = new GridLength(300, GridUnitType.Pixel);
    private GridLength leftSplitterWidth = new GridLength(8, GridUnitType.Pixel);
    private GridLength rightColumnWidth = new GridLength(360, GridUnitType.Pixel);
    private GridLength rightSplitterWidth = new GridLength(8, GridUnitType.Pixel);
    private bool bucketHeadingUpdating;
    private bool landingShowingTemplates;
    private bool landingShowingConsumable;
    private bool landingShowArchived;
    private IReadOnlyList<ZetlProjectSummary> lastProjectSummaries = [];
    private bool suppressLandingProjectSelection;
    private string? inspectedSlipId;
    private int pictureRenderGeneration;
    private long pictureCacheBytes;
    // The center View is one addressable per-slip document: each slip id maps to its
    // rendered block so the tree can scroll/highlight it and a block click can select
    // it back in the tree. The signature lets a pure selection change skip a rebuild
    // (so picture blocks don't reload) while content changes still re-render.
    private readonly Dictionary<string, Border> viewSlipBlocks = new(StringComparer.Ordinal);
    private string lastViewSignature = "";
    private string? highlightedViewSlipId;
    // The project the center View was last built for, so an in-place rebuild
    // (e.g. hiding a slip) preserves scroll while switching projects resets it.
    private string? lastViewerProjectId;
    private bool allowWindowClose;

    public MainWindow()
    {
        InitializeComponent();
        connection = null!;
    }

    public MainWindow(KastnConnectionController connection)
    {
        this.connection = connection;
        InitializeComponent();
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
            if (CurrentAppSettings().KastnAutosave)
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
        boldButton.Click += async (_, _) => await ToggleSlipStyleAsync(ZetlInlineStyleKinds.Bold);
        italicButton.Click += async (_, _) => await ToggleSlipStyleAsync(ZetlInlineStyleKinds.Italic);
        strikeButton.Click += async (_, _) => await ToggleSlipStyleAsync(ZetlInlineStyleKinds.Strike);
        codeButton.Click += async (_, _) => await ToggleInlineStyleAsync(ZetlInlineStyleKinds.Code);
        linkButton.Click += async (_, _) => await SetEditorWebLinkAsync();
        wikiLinkButton.Click += async (_, _) => await InsertSlipLinkAsync();
        ignoreBucketRenderKindCheck.IsCheckedChanged += async (_, _) => await OnIgnoreBucketRenderKindChangedAsync();
        representationToggleButton.Click += async (_, _) => await ToggleSlipRepresentationAsync();
        bulletListButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Bullet);
        numberListButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Ordered);
        taskListButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Task);
        headingButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Heading);
        quoteButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Quote);
        codeBlockButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Code);
        insertDividerButton.Click += async (_, _) => await InsertDividerSlipAsync();
        insertGroupButton.Click += async (_, _) => await InsertGroupBucketAsync();
        globalViews = viewStore.LoadAll();
        loadedViews = globalViews;
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
        viewListStyleBox.ItemsSource = ZetlViewListStyles.All;
        viewFormattedKindButton.Click += (_, _) => SetViewEditorKind(ZetlViewKinds.Formatted);
        viewPlainKindButton.Click += (_, _) => SetViewEditorKind(ZetlViewKinds.Plain);
        viewTsvKindButton.Click += (_, _) => SetViewEditorKind(ZetlViewKinds.Tsv);
        viewMarkdownKindButton.Click += (_, _) => SetViewEditorKind(ZetlViewKinds.Markdown);
        viewHtmlKindButton.Click += (_, _) => SetViewEditorKind(ZetlViewKinds.Html);
        viewPdfKindButton.Click += (_, _) => SetViewEditorKind(ZetlViewKinds.Pdf);
        viewAllBucketsButton.Click += (_, _) => SetViewStructureMode(custom: false);
        viewCustomSectionsButton.Click += (_, _) => SetViewStructureMode(custom: true);
        addViewSectionButton.Click += (_, _) => AddViewSection();
        viewNameBox.TextChanged += (_, _) => RefreshViewLivePreview();
        viewDescriptionBox.TextChanged += (_, _) => RefreshViewLivePreview();
        viewListStyleBox.SelectionChanged += (_, _) => RefreshViewLivePreview();
        viewNumberHeadingsCheck.IsCheckedChanged += (_, _) => RefreshViewLivePreview();
        viewTitleBox.TextChanged += (_, _) => RefreshViewLivePreview();
        viewShowTitleCheck.IsCheckedChanged += (_, _) => RefreshViewLivePreview();
        viewTsvRowBox.ValueChanged += (_, _) => RefreshViewLivePreview();
        saveViewSettingsButton.Click += async (_, _) => await SaveViewAsync();
        cancelViewSettingsButton.Click += async (_, _) => await CancelViewEditAsync();
        viewKindBox.SelectionChanged += (_, _) =>
        {
            if (!viewEditorUpdating)
            {
                ApplyViewKindSettingsVisibility();
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
        landingCurrentProjectsButton.Click += (_, _) => SetArchivedProjectMode(showArchived: false);
        landingArchivedProjectsButton.Click += (_, _) => SetArchivedProjectMode(showArchived: true);
        landingCaptureButton.Click += (_, _) => SetTemplateType(consumable: false);
        landingConsumableButton.Click += (_, _) => SetTemplateType(consumable: true);
        landingCreationItems.ItemsSource = creations;
        WireTemplateEditor();
        WireCreationEditor();
        SizeChanged += (_, _) => RefreshLandingGridLayout();
        KeyDown += OnKeyDown;
        Closing += OnWindowClosing;
        Closed += (_, _) =>
        {
            connection.SnapshotChanged -= OnSnapshotChanged;
            ClearDecodedPictureCache();
        };
        ApplySnapshot(connection.Current);
    }

    public async void ActivateRequest(string? projectId)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        ShowInTaskbar = true;
        Show();
        Activate();
        BringToForeground();
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            await connection.NavigateToProjectAsync(projectId);
        }
    }

    // Minimize remains normal window-manager behavior. Closing either hides
    // Kastn behind Zetl's tray icon or exits the Kastn process, depending on the
    // shared Kastn close preference.
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (allowWindowClose || !CurrentAppSettings().KastnCloseToTray)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        ShowInTaskbar = false;
        Hide();
    }

    // Called from the control pipe (off the UI thread) when Zetl is quitting and
    // wants Kastn to close too. Kastn raises itself from hidden or minimized state
    // and confirms. Returns true to close, false to keep both apps running.
    public Task<bool> RequestShutdownDecisionAsync()
    {
        var decided = new TaskCompletionSource<bool>();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                decided.SetResult(await DecideShutdownAsync());
            }
            catch (Exception ex)
            {
                decided.SetException(ex);
            }
        });
        return decided.Task;
    }

    private async Task<bool> DecideShutdownAsync()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (!IsVisible)
        {
            Show();
        }

        ShowInTaskbar = true;
        Activate();
        BringToForeground();
        return await KastnDialogs.ConfirmAsync(
            this,
            "Closing Zetl will also close Kastn. Close both apps?",
            "Close both");
    }

    public void CloseForShutdown()
    {
        Dispatcher.UIThread.Post(() =>
        {
            allowWindowClose = true;
            Close();
        });
    }

    private void OnSnapshotChanged(object? sender, KastnSessionSnapshot snapshot)
    {
        // During a batch (e.g. aligning or numbering many slips) the service publishes
        // a snapshot per mutation; applying each would rebuild the tree and View N
        // times in sequence. Drop the intermediate pushes — the batch does one
        // RefreshAsync at the end.
        if (batching)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(KastnSessionSnapshot snapshot)
    {
        var priorProjectId = currentProject?.Id;
        var selectedProjectId = snapshot.Project?.Id;
        var selectedBucketId = SelectedBucketId;
        var selectedSlipId = pendingSlipSelectionId ?? editorState.SlipId;

        // Undo/redo entries hold revisions captured against the open project. Drop
        // them when the open project changes or the live connection drops, since those
        // revisions go stale after a resync.
        if (!string.Equals(priorProjectId, selectedProjectId, StringComparison.Ordinal)
            || snapshot.ConnectionState != KastnConnectionState.Online)
        {
            ClearUndoHistory();
        }

        // Decoded pictures belong to the outgoing project.
        if (!string.Equals(priorProjectId, selectedProjectId, StringComparison.Ordinal))
        {
            ClearDecodedPictureCache();
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
                // The catalog only changes with the project's metadata revision
                // (view saves/deletes bump it; slip captures do not), so skip the
                // disk reload and picker reset on ordinary mutations. The view
                // editors refresh the catalog explicitly when they save.
                var viewCatalogSignature = $"{projectSnapshot.Id}|{projectSnapshot.MetadataRevision}";
                if (!string.Equals(viewCatalogSignature, lastViewCatalogSignature, StringComparison.Ordinal))
                {
                    lastViewCatalogSignature = viewCatalogSignature;
                    RefreshViewCatalog(projectSnapshot, selectedViewId);
                }
                if (!string.Equals(priorProjectId, projectSnapshot.Id, StringComparison.Ordinal))
                {
                    // Remember the opened project for the "reopen last project" startup
                    // preference (Kastn-owned state, not the shared settings file).
                    stateStore.LastProjectId = projectSnapshot.Id;
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
                projectTreeRoots.Clear();
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
        landingProjectWorkspaceList.SelectedItem = null;
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
        // The toggle stays visible even with no visible projects, so archived-only
        // workspaces can still reveal their projects.
        landingProjectsPanel.IsVisible = showChoices;
        landingLowerContent.IsVisible = showChoices;
        landingProjectLibraryHeader.IsVisible = showChoices;
        landingProjectList.IsVisible = showChoices && recentProjects.Count > 0;
        landingProjectWorkspaceList.IsVisible = showChoices && showProjects && projects.Count > 0;
        landingTemplateList.IsVisible = showChoices && landingShowingTemplates;
        landingTemplateList.IsEnabled = IsOnline;
        landingCreationList.IsVisible = showChoices && landingShowingCreations;
        landingCreationList.IsEnabled = IsOnline;
        landingLowerActionRow.IsVisible = showChoices
            && (showProjects || landingShowingTemplates || landingShowingCreations);
        landingProjectArchiveToggle.IsVisible = showChoices && showProjects;
        landingTemplateTypeToggle.IsVisible = showChoices && landingShowingTemplates;
        landingTemplateTypeToggle.IsEnabled = IsOnline;
        landingNewTemplateButton.IsVisible = showChoices && landingShowingTemplates;
        landingNewCreationButton.IsVisible = showChoices && landingShowingCreations;
        landingWorkspaceTitle.Text = landingShowingCreations
            ? "Create"
            : landingShowingTemplates ? "Templates" : "Projects";
        landingWorkspaceSubtitle.Text = landingShowingCreations
            ? "Start from a saved creation type."
            : landingShowingTemplates
                ? "Start a project from a reusable template."
                : "Open a project or manage the library.";
        landingProjectsButton.IsEnabled = !showProjects;
        landingTemplatesButton.IsEnabled = !landingShowingTemplates;
        landingCurrentProjectsButton.IsEnabled = landingShowArchived;
        landingArchivedProjectsButton.IsEnabled = !landingShowArchived;
        landingCreateButton.IsVisible = showChoices;
        landingCreateButton.IsEnabled = !landingShowingCreations;
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
        var allProjects = new List<ProjectListItem>();
        foreach (var project in lastProjectSummaries)
        {
            var pinned = stateStore.IsPinned(project.Id);
            allProjects.Add(CreateProjectListItem(project, pinned));
        }

        AddLaneCard(allProjects, ZetlStateStore.NormalLane, LaneLabel(ZetlStateStore.NormalLane));
        AddLaneCard(allProjects, ZetlStateStore.ShiftLane, LaneLabel(ZetlStateStore.ShiftLane));

        var laneProjectIds = laneCards
            .SelectMany(card => new[] { card.Project?.Id, card.OverlayProject?.Id })
            .Where(id => id is not null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var project in allProjects.Where(project =>
            !project.IsTemporary
            && !laneProjectIds.Contains(project.Id)
            && project.IsArchived == landingShowArchived))
        {
            projects.Add(project);
        }

        var ordered = projects
            .OrderBy(project => project.SectionRank)
            .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        projects.Clear();
        foreach (var project in ordered)
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

    private static ProjectListItem CreateProjectListItem(
        ZetlProjectSummary project,
        bool pinned)
    {
        return new ProjectListItem(
            project.Id,
            project.Name,
            project.MetadataRevision,
            LandingProjectSection(project, pinned),
            LandingProjectSectionRank(project, pinned),
            LandingProjectDetail(project),
            string.IsNullOrWhiteSpace(project.PreviewText)
                ? "No slips yet"
                : project.PreviewText,
            LandingProjectActivity(project),
            project.LastActivityUtc,
            project.Status,
            project.VisibleSlipCount,
            pinned,
            project.ActiveLane,
            project.UnderlyingLane,
            project.CanCreateTemporaryFromReplay,
            string.Equals(
                project.Kind,
                ZetlStateStore.TemporaryConsumableProjectKind,
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
        if (string.Equals(project.Kind, ZetlStateStore.TemporaryConsumableProjectKind, StringComparison.Ordinal))
        {
            detail = $"Temporary | {detail}";
        }

        return project.DeletedSlipCount == 0
            ? detail
            : $"{detail} | {project.DeletedSlipCount} deleted";
    }

    private static string LandingProjectSection(ZetlProjectSummary project, bool pinned)
    {
        if (pinned)
        {
            return "Pinned";
        }

        if (string.Equals(project.ActiveLane, ZetlStateStore.NormalLane, StringComparison.Ordinal))
        {
            return "Main";
        }

        if (string.Equals(project.ActiveLane, ZetlStateStore.ShiftLane, StringComparison.Ordinal))
        {
            return "Alternate";
        }

        return "Projects";
    }

    private static int LandingProjectSectionRank(ZetlProjectSummary project, bool pinned)
    {
        if (pinned)
        {
            return 0;
        }

        if (string.Equals(project.ActiveLane, ZetlStateStore.NormalLane, StringComparison.Ordinal))
        {
            return 1;
        }

        if (string.Equals(project.ActiveLane, ZetlStateStore.ShiftLane, StringComparison.Ordinal))
        {
            return 2;
        }

        return 3;
    }

    private static string LandingProjectActivity(ZetlProjectSummary project)
    {
        return project.LastActivityUtc is null
            ? "No activity yet"
            : $"Last slip {project.LastActivityUtc.Value.LocalDateTime:g}";
    }

    private string LaneLabel(string lane)
    {
        var settings = CurrentAppSettings();
        return string.Equals(lane, ZetlStateStore.ShiftLane, StringComparison.Ordinal)
            ? ResolveLaneLabel(settings.KastnAlternateLaneLabel, "Alternate")
            : ResolveLaneLabel(settings.KastnMainLaneLabel, "Main");
    }

    private static string ResolveLaneLabel(string? value, string fallback)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return fallback;
        }

        return trimmed.Length <= LaneLabelMaxLength
            ? trimmed
            : trimmed[..LaneLabelMaxLength].TrimEnd();
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

    private void RefreshBuckets(ZetlProjectSnapshot project, string? selectedBucketId)
    {
        // Capture the whole current selection (tree node ids) before the rebuild, so
        // an action that refreshes keeps the entire group selected — not just the
        // first node. Runs inside ApplySnapshot's refreshing guard, so re-selecting
        // drives no side effects here.
        var rememberedIds = projectTree.SelectedItems?.OfType<KastnTreeNode>()
            .Select(node => node.Id)
            .ToList() ?? [];

        // Merge the fresh projection into the live tree instead of resetting
        // ItemsSource: unchanged rows keep their realized containers (a full
        // reset re-created every row — about a second per refresh on a
        // few-hundred-slip project), and selection/expansion survive naturally.
        ReconcileTreeLevel(
            projectTreeRoots,
            KastnWorkbench.BuildProjectTree(project, project.Slips, deletedOnly: showingDeleted));
        if (!ReferenceEquals(projectTree.ItemsSource, projectTreeRoots))
        {
            projectTree.ItemsSource = projectTreeRoots;
        }

        UpdateDeletedToggle(project);
        var treeNodes = projectTree.ItemsSource as IEnumerable<KastnTreeNode>;

        // A pending override (after creating/moving a slip) wins; else restore the
        // remembered group; else fall back to the requested bucket / first slip /
        // first bucket so the editor and tree agree on open.
        IReadOnlyList<string> restoreIds;
        if (pendingSlipSelectionId is { } pending && FindTreeNode(treeNodes, pending) is not null)
        {
            restoreIds = [pending];
        }
        else
        {
            var present = rememberedIds.Where(id => FindTreeNode(treeNodes, id) is not null).ToList();
            restoreIds = present.Count > 0
                ? present
                : (selectedBucketId
                    ?? FirstSlipNode(treeNodes)?.Id
                    ?? project.Buckets.FirstOrDefault()?.Id) is { } fallback
                        ? [fallback]
                        : [];
        }

        ApplyTreeNodeSelection(treeNodes, restoreIds);
        RefreshBucketEditor();
        RefreshDestinationBuckets();
        SetDetailPaneMode(detailShowingMetadata);
    }

    // The live tree roots the TreeView stays bound to across refreshes.
    private readonly System.Collections.ObjectModel.ObservableCollection<KastnTreeNode> projectTreeRoots = [];

    // Make one level of the live tree match the freshly built projection: reuse a
    // node with the same id (updating its display state in place and recursing
    // into its children), insert new ones, drop stale ones, and move the rest
    // into order with minimal collection churn.
    private static void ReconcileTreeLevel(
        System.Collections.ObjectModel.ObservableCollection<KastnTreeNode> current,
        IReadOnlyList<KastnTreeNode> desired)
    {
        var desiredIds = new HashSet<string>(desired.Select(node => node.Id), StringComparer.Ordinal);
        for (var i = current.Count - 1; i >= 0; i--)
        {
            if (!desiredIds.Contains(current[i].Id))
            {
                current.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var want = desired[i];
            var existingIndex = -1;
            for (var j = i; j < current.Count; j++)
            {
                if (string.Equals(current[j].Id, want.Id, StringComparison.Ordinal))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                current.Insert(i, want);
                continue;
            }

            var node = current[existingIndex];
            if (node.Kind != want.Kind)
            {
                current[existingIndex] = want;
            }
            else
            {
                node.UpdateFrom(want);
                ReconcileTreeLevel(node.Children, want.Children);
            }

            if (existingIndex != i)
            {
                current.Move(existingIndex, i);
            }
        }
    }

    // Re-select a set of tree nodes by id: a single node sets SelectedItem, several
    // populate SelectedItems (multi). Runs under the refreshing guard.
    private void ApplyTreeNodeSelection(IEnumerable<KastnTreeNode>? treeNodes, IReadOnlyList<string> ids)
    {
        var nodes = ids
            .Select(id => FindTreeNode(treeNodes, id))
            .OfType<KastnTreeNode>()
            .ToList();
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

    // The selected bucket when it is in "title mode" (a single bucket selected and
    // not expanded to its slips), else null. In this mode the toolbar's alignment and
    // the heading panel act on the bucket's title rather than its slips.
    private ZetlBucketSnapshot? TitleModeBucket()
    {
        return CurrentSelection() is KastnSelection.BucketTitle { BucketId: var bucketId }
            && currentProject?.Buckets.FirstOrDefault(bucket => bucket.Id == bucketId) is { } found
            && !KastnWorkbench.IsDeletedBucket(found)
            ? found
            : null;
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
            await connection.RefreshAsync();
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
        SetEditingEnabled();
    }

    private void RefreshSlipView(bool force = false)
    {
        if ((!force && refreshing) || currentProject is null)
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

            // Bind the editor from the explicit selection: a batch clears it; a
            // pending/just-created or surviving single slip loads it; otherwise (and
            // not in title mode) default to the first slip.
            var selected = slips.FirstOrDefault(item => item.Id == selectedId);
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
            else if (!editorState.IsDirty && editorState.ConflictCurrent is null && TitleModeBucket() is null)
            {
                editorState.Select(slips.FirstOrDefault()?.Slip);
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
                if (editorState.SlipId is null)
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
            // Zetl coldkey and never reaches Kastn.
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
            // held Ctrl+Shift+Z is Zetl's Shift-lane undo coldkey, and mapping its tap
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
        connection.Current.ConnectionState == KastnConnectionState.Online;

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
                : currentProject.Buckets.FirstOrDefault(bucket => bucket.Id == node.Slip!.BucketId);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    private sealed record ProjectListItem(
        string Id,
        string Name,
        long MetadataRevision,
        string Section,
        int SectionRank,
        string Detail,
        string PreviewText,
        string ActivityText,
        DateTimeOffset? LastActivityUtc,
        string Status,
        int VisibleSlipCount,
        bool IsPinned,
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
            IsActive && !string.Equals(ActiveLane, ZetlStateStore.NormalLane, StringComparison.Ordinal);

        public bool CanSetAlternateActive =>
            IsActive && !string.Equals(ActiveLane, ZetlStateStore.ShiftLane, StringComparison.Ordinal);

        public string SetActiveActionLabel =>
            string.Equals(ActiveLane, ZetlStateStore.NormalLane, StringComparison.Ordinal)
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
