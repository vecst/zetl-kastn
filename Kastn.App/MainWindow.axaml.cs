using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow : Window
{
    private readonly KastnConnectionController connection;
    private readonly ObservableCollection<ProjectListItem> projects = [];
    private readonly ObservableCollection<KastnBucketItem> buckets = [];
    private readonly ObservableCollection<SlipListItem> slips = [];
    private readonly ObservableCollection<FilterItem> sources = [];
    private readonly ObservableCollection<FilterItem> sessions = [];
    private readonly ObservableCollection<DateFilterItem> dates = [];
    private readonly ObservableCollection<KastnBucketItem> parentBuckets = [];
    private readonly ObservableCollection<KastnBucketItem> moveBuckets = [];
    private readonly KastnEditorState editorState = new();
    private readonly DispatcherTimer autosaveTimer;
    private ZetlProjectSnapshot? currentProject;
    private bool refreshing;
    private bool editorUpdating;
    private bool saving;
    private string? pendingSaveText;
    private string? pendingBucketSelectionId;

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
        projectList.ItemsSource = projects;
        bucketList.ItemsSource = buckets;
        slipList.ItemsSource = slips;
        sourceFilterBox.ItemsSource = sources;
        sessionFilterBox.ItemsSource = sessions;
        dateFilterBox.ItemsSource = dates;
        parentBucketBox.ItemsSource = parentBuckets;
        moveBucketBox.ItemsSource = moveBuckets;

        dates.Add(new DateFilterItem(KastnDateFilter.All, "All time"));
        dates.Add(new DateFilterItem(KastnDateFilter.Today, "Today"));
        dates.Add(new DateFilterItem(KastnDateFilter.Last7Days, "Last 7 days"));
        dates.Add(new DateFilterItem(KastnDateFilter.Last30Days, "Last 30 days"));
        dateFilterBox.SelectedIndex = 0;

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
        projectList.SelectionChanged += OnProjectSelectionChanged;
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
        exitMenuItem.Click += (_, _) => Close();
        saveSlipMenuItem.Click += async (_, _) => await SaveEditorAsync();
        deleteSlipMenuItem.Click += async (_, _) => await DeleteSlipAsync();
        focusSearchMenuItem.Click += (_, _) => searchBox.Focus();
        focusProjectsMenuItem.Click += (_, _) => projectList.Focus();
        focusBucketsMenuItem.Click += (_, _) => bucketList.Focus();
        focusSlipsMenuItem.Click += (_, _) => slipList.Focus();
        aboutMenuItem.Click += ShowAbout;
        addBucketButton.Click += async (_, _) => await AddBucketAsync();
        saveBucketButton.Click += async (_, _) => await SaveBucketAsync();
        deleteBucketButton.Click += async (_, _) => await DeleteBucketAsync();
        saveSlipButton.Click += async (_, _) => await SaveEditorAsync();
        deleteSlipButton.Click += async (_, _) => await DeleteSlipAsync();
        moveSlipButton.Click += async (_, _) => await MoveSlipAsync();
        useZetlButton.Click += (_, _) => UseZetlVersion();
        keepMineButton.Click += async (_, _) => await KeepMineAsync();
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
        var selectedSlipId = editorState.SlipId;

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

            projectList.SelectedItem = projects.FirstOrDefault(
                project => project.Id == selectedProjectId);
            projectCountText.Text = snapshot.Projects.Count switch
            {
                0 => "No projects",
                1 => "1 project",
                var count => $"{count} projects"
            };

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
                RefreshSlipView();
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
                projectView.IsVisible = false;
                emptyState.IsVisible = true;
                emptyStateText.Text = snapshot.ConnectionState == KastnConnectionState.Online
                    ? "No projects yet. Create one in Zetl, then it will appear here."
                    : snapshot.Status;
            }

            SetConnectionState(snapshot);
        }
        finally
        {
            refreshing = false;
        }
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
        bucketNameBox.Text = selected?.Name ?? "";
        bucketNameBox.IsEnabled = selected is not null && IsOnline;
        saveBucketButton.IsEnabled = selected is not null && IsOnline;
        deleteBucketButton.IsEnabled = selected is not null && IsOnline;

        parentBuckets.Clear();
        parentBuckets.Add(new KastnBucketItem(null, "No parent", null));
        if (currentProject is { } project)
        {
            foreach (var item in KastnWorkbench.BuildBucketHierarchy(project)
                .Where(item => selected is null
                    || (item.Id != selected.Id && !IsDescendant(project, item.Id!, selected.Id))))
            {
                parentBuckets.Add(item);
            }
        }

        parentBucketBox.SelectedItem = parentBuckets.FirstOrDefault(
                item => item.Id == selected?.ParentBucketId)
            ?? parentBuckets[0];
        parentBucketBox.IsEnabled = selected is not null && IsOnline;
    }

    private void RefreshDestinationBuckets()
    {
        var selectedSlip = SelectedSlip;
        moveBuckets.Clear();
        if (currentProject is { } project)
        {
            foreach (var item in KastnWorkbench.BuildBucketHierarchy(project)
                .Where(item => item.Id != selectedSlip?.BucketId))
            {
                moveBuckets.Add(item);
            }
        }

        moveBucketBox.SelectedIndex = moveBuckets.Count > 0 ? 0 : -1;
        SetEditingEnabled();
    }

    private void RefreshSlipView(bool force = false)
    {
        if ((!force && refreshing) || currentProject is null)
        {
            return;
        }

        var selectedId = editorState.SlipId;
        var filtered = KastnWorkbench.FilterSlips(
            currentProject,
            SelectedBucketId,
            (sourceFilterBox.SelectedItem as FilterItem)?.Value,
            (sessionFilterBox.SelectedItem as FilterItem)?.Value,
            (dateFilterBox.SelectedItem as DateFilterItem)?.Value ?? KastnDateFilter.All,
            searchBox.Text,
            DateTimeOffset.Now);

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
                    slip.Text,
                    $"{bucketName} | {slip.Source} | {slip.CapturedAtUtc.LocalDateTime:g}",
                    slip));
            }

            slipCountText.Text = $"{slips.Count} of {currentProject.Slips.Count} slips";
            var selected = slips.FirstOrDefault(item => item.Id == selectedId);
            if (selected is not null)
            {
                slipList.SelectedItem = selected;
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

            RefreshDestinationBuckets();
        }
        finally
        {
            refreshing = wasRefreshing;
        }
    }

    private async void OnProjectSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!refreshing && projectList.SelectedItem is ProjectListItem project)
        {
            if (!await SaveEditorAsync())
            {
                refreshing = true;
                projectList.SelectedItem = projects.FirstOrDefault(
                    item => item.Id == currentProject?.Id);
                refreshing = false;
                return;
            }

            await connection.NavigateToProjectAsync(project.Id);
        }
    }

    private async void OnSlipSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (refreshing || slipList.SelectedItem is not SlipListItem selected)
        {
            return;
        }

        var oldId = editorState.SlipId;
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
            statusText.Text = "A slip cannot be saved with empty text.";
            return false;
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

        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.AddBucket,
            new AddBucketCommand
            {
                Name = name,
                ParentBucketId = SelectedBucketId
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

        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.DeleteBucket,
            new DeleteBucketCommand(),
            currentProject.Id,
            bucket.Id,
            bucket.Revision));
        HandleSimpleResponse(response, "Bucket deleted.");
    }

    private async Task MoveSlipAsync()
    {
        if (!await SaveEditorAsync()
            || !IsOnline
            || currentProject is null
            || SelectedSlip is not { } slip
            || moveBucketBox.SelectedItem is not KastnBucketItem destination
            || destination.Id is null)
        {
            return;
        }

        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.MoveSlip,
            new MoveSlipCommand { DestinationBucketId = destination.Id },
            currentProject.Id,
            slip.Id,
            editorState.Revision));
        HandleSimpleResponse(response, $"Slip moved to {destination.Bucket?.Name}.");
    }

    private async Task DeleteSlipAsync()
    {
        if (!IsOnline || currentProject is null || SelectedSlip is not { } slip)
        {
            return;
        }

        if (!await KastnDialogs.ConfirmAsync(
                this,
                "Delete the selected slip?",
                "Delete Slip"))
        {
            return;
        }

        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.DeleteSlip,
            new DeleteSlipCommand(),
            currentProject.Id,
            slip.Id,
            editorState.Revision));
        if (response.Status == ZetlResponseStatus.Success)
        {
            editorState.Select(null);
            UpdateEditorFromState();
        }

        HandleSimpleResponse(response, "Slip deleted.");
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

    private void UpdateEditorFromState()
    {
        editorUpdating = true;
        slipEditor.Text = editorState.DraftText;
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
            : $"{slip.Source} | {ShortSession(slip.SessionId)} | "
                + $"{slip.CapturedAtUtc.LocalDateTime:F} | revision {editorState.Revision}";
        SetEditingEnabled();
    }

    private void SetEditingEnabled()
    {
        var canEdit = IsOnline && editorState.SlipId is not null && !saving;
        slipEditor.IsEnabled = canEdit && editorState.ConflictCurrent is null;
        saveSlipButton.IsEnabled = canEdit && editorState.ConflictCurrent is null;
        saveSlipMenuItem.IsEnabled = saveSlipButton.IsEnabled;
        deleteSlipButton.IsEnabled = canEdit;
        deleteSlipMenuItem.IsEnabled = canEdit;
        moveSlipButton.IsEnabled = canEdit && moveBuckets.Count > 0;
        moveBucketBox.IsEnabled = moveSlipButton.IsEnabled;
        addBucketButton.IsEnabled = IsOnline && currentProject is not null;
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

    private ZetlSlipSnapshot? SelectedSlip =>
        (slipList.SelectedItem as SlipListItem)?.Slip
        ?? currentProject?.Slips.FirstOrDefault(slip => slip.Id == editorState.SlipId);

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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    private sealed record ProjectListItem(string Id, string Name, string Detail);
    private sealed record SlipListItem(
        string Id,
        string Text,
        string Detail,
        ZetlSlipSnapshot Slip);
    private sealed record FilterItem(string? Value, string Label);
    private sealed record DateFilterItem(KastnDateFilter Value, string Label);
}
