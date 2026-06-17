using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow : Window
{
    private const string UntitledSlipText = "Untitled";
    private const double LandingCardWidth = 250;
    private const double LandingCardHeight = 190;
    private const double LandingCardMargin = 8;
    private const int LandingMaxColumns = 6;
    private const int LandingMaxRows = 5;

    private readonly KastnConnectionController connection;
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
        landingTemplateList.ItemsSource = templates;
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
        templates.Add(new TemplateListItem("Blank", "Empty project", "Start with a clean bucket structure."));
        templates.Add(new TemplateListItem("Writing", "Draft stack", "Collect notes toward a draft or essay."));
        templates.Add(new TemplateListItem("Research", "Research board", "Track sources, notes, and synthesis."));

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
        saveSlipButton.Click += async (_, _) => await SaveEditorAsync();
        deleteSlipButton.Click += async (_, _) => await DeleteSlipAsync();
        moveSlipButton.Click += async (_, _) => await MoveSlipAsync();
        restoreSlipButton.Click += async (_, _) => await RestoreSlipAsync();
        useZetlButton.Click += (_, _) => UseZetlVersion();
        keepMineButton.Click += async (_, _) => await KeepMineAsync();
        landingProjectsButton.Click += (_, _) => SetLandingMode(showTemplates: false);
        landingTemplatesButton.Click += (_, _) => SetLandingMode(showTemplates: true);
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
                    $"{project.BucketCount} buckets, {project.SlipCount} slips"));
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
                    && (snapshot.Projects.Count > 0 || templates.Count > 0);
                if (snapshot.Projects.Count == 0 && templates.Count > 0)
                {
                    landingShowingTemplates = true;
                }

                landingModeToggle.IsVisible = showLandingChoices;
                emptyStateText.Text = snapshot.ConnectionState == KastnConnectionState.Online
                    ? "Select a project or template to begin."
                    : snapshot.Status;
                RefreshLandingMode();
            }

            SetConnectionState(snapshot);
        }
        finally
        {
            refreshing = false;
        }
    }

    private void SetLandingMode(bool showTemplates)
    {
        landingShowingTemplates = showTemplates;
        landingProjectList.SelectedItem = null;
        RefreshLandingMode();
    }

    private void RefreshLandingMode()
    {
        var showChoices = emptyState.IsVisible && landingModeToggle.IsVisible;
        landingProjectList.IsVisible = showChoices && !landingShowingTemplates && projects.Count > 0;
        landingTemplateList.IsVisible = showChoices && landingShowingTemplates;
        landingProjectsButton.IsEnabled = landingShowingTemplates;
        landingTemplatesButton.IsEnabled = !landingShowingTemplates;
        RefreshLandingGridLayout();
    }

    private void RefreshLandingGridLayout()
    {
        var cardOuterWidth = LandingCardWidth + (LandingCardMargin * 2);
        var cardOuterHeight = LandingCardHeight + (LandingCardMargin * 2);
        var cardCount = Math.Max(1, landingShowingTemplates ? templates.Count : projects.Count);

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
        if (!refreshing
            && sender is ListBox listBox
            && listBox.SelectedItem is ProjectListItem project)
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

        if (!await SaveEditorAsync())
        {
            statusText.Text = "Save or resolve the current slip before deleting the project.";
            return;
        }

        var project = currentProject;
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
            editorState.Select(null);
            UpdateEditorFromState();
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

        viewerTextBox.Text = visible.Count == 0
            ? ""
            : KastnWorkbench.BuildViewerText(currentProject, visible);
        RefreshEditableViewStatus();
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
                                new TextBlock
                                {
                                    [Grid.ColumnProperty] = 2,
                                    Classes = { "muted" },
                                    FontSize = 18,
                                    FontWeight = FontWeight.SemiBold,
                                    Text = $"{index + 1}/{total}",
                                    VerticalAlignment = VerticalAlignment.Center
                                }
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

    private void RefreshEditableViewStatus()
    {
        editViewButton.Content = editableViewMode ? "Read View" : "Edit View";
        editViewMoveBucketBox.IsVisible = editableViewMode;
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
            ? EditableSessionStatus()
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

    private sealed record ProjectListItem(string Id, string Name, string Detail);

    private sealed record TemplateListItem(string Kind, string Name, string Detail);
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
            BucketLabel = bucketLabel;
            Source = slip.Source;
            CapturedAtUtc = slip.CapturedAtUtc;
            BaselineText = slip.Text;
            DraftText = slip.Text;
            Title = SlipPreviewText(slip);
        }

        public string SlipId { get; }
        public long Revision { get; private set; }
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
