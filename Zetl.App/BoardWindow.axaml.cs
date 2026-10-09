using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Media.Imaging;

namespace ZETL;

internal partial class BoardWindow : ZetlPopupWindow
{
    private readonly ZetlStateStore store = null!;
    private readonly ZetlTemplateStore templateStore = null!;
    private readonly bool shiftedLane;
    private readonly Action<string>? openInKastn;
    private readonly Func<ZetlTemplateDocument, string, bool, ZetlProject?>? createProjectFromTemplate;
    private bool refreshing;
    private bool childDialogOpen;
    // The slip in the editor and the text it was loaded with.
    private readonly ZetlBoardNoteDraft noteDraft = new();
    private List<BoardNoteItem> noteItems = [];
    private Bitmap? selectedImagePreview;

    // Non-null while a new note is being composed: an in-memory draft that lives
    // only in the editor and is not added to the store until it has non-blank
    // text. This is what keeps the Board from persisting empty notes.
    private ZetlBucket? composingBucket;

    // The Board is large, so on a small display shed extra space rather than
    // filling the whole screen.
    protected override double CompactWidthReduction => 175;

    protected override double CompactHeightReduction => 150;

    // The foreground window to restore when a gesture-opened Board closes (null
    // for a tray-opened Board, which should not steal focus back).
    public object? ForegroundTarget { get; set; }

    public BoardWindow()
    {
        InitializeComponent();
    }

    internal BoardWindow(
        ZetlStateStore store,
        bool shiftedLane = false,
        ZetlTemplateStore? templateStore = null,
        Action<string>? openInKastn = null,
        Func<ZetlTemplateDocument, string, bool, ZetlProject?>? createProjectFromTemplate = null)
    {
        this.store = store;
        this.shiftedLane = shiftedLane;
        this.templateStore = templateStore ?? new ZetlTemplateStore();
        this.openInKastn = openInKastn;
        this.createProjectFromTemplate = createProjectFromTemplate;
        InitializeComponent();

        Title = shiftedLane ? "Zetl Board - Shift" : "Zetl Board";
        bucketKindBox.ItemsSource = new[] { "Standard", "Replay" };

        projectBox.SelectionChanged += (_, _) =>
        {
            if (!refreshing)
            {
                SaveEditingNote();
                RefreshSelectedProject();
            }
        };
        activeProjectBox.IsCheckedChanged += (_, _) => ToggleActiveProjectFromControl();
        bucketList.SelectionChanged += (_, _) =>
        {
            if (refreshing || ActiveProject is null || ActiveBucket is not { } bucket)
            {
                return;
            }

            SaveEditingNote();
            store.SetActiveBucket(ActiveProject, bucket.Id);
        };
        bucketList.DoubleTapped += async (_, _) => await ShowBucketSettingsAsync();
        noteList.SelectionChanged += (_, _) =>
        {
            if (!refreshing)
            {
                SaveEditingNote();
                RefreshSelectedNote();
            }
        };
        bucketKindBox.SelectionChanged += (_, _) =>
        {
            if (!refreshing
                && ActiveBucket is { } bucket
                && bucketKindBox.SelectedItem is string kind)
            {
                store.SetBucketKind(bucket, kind);
            }
        };

        newProjectButton.Click += async (_, _) => await AddProjectAsync();
        Activated += (_, _) => RefreshProjectCreationFlyout();
        deleteProjectButton.Click += async (_, _) => await DeleteProjectAsync();
        reactivateProjectButton.Click += (_, _) =>
        {
            if (!refreshing
                && ActiveProject is { } project
                && !ZetlStateRules.IsActiveStatus(project))
            {
                // The store raises Changed, so OnStoreChanged refreshes the Board.
                store.SetProjectStatus(project, ZetlStateRules.ActiveStatus);
            }
        };
        openKastnButton.Click += (_, _) =>
        {
            if (ActiveProject is { } project)
            {
                this.openInKastn?.Invoke(project.Id);
            }
        };
        exportProjectButton.Click += async (_, _) => await ExportProjectAsync();
        saveProjectButton.Click += (_, _) => SaveProjectName();
        addBucketButton.Click += async (_, _) => await AddBucketAsync();
        deleteBucketButton.Click += async (_, _) => await DeleteBucketAsync();
        saveBucketButton.Click += (_, _) => SaveBucketName();
        bucketSettingsButton.Click += async (_, _) => await ShowBucketSettingsAsync();
        bucketNameBox.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                args.Handled = true;
                SaveBucketName();
            }
        };
        noteEditor.LostFocus += (_, _) => SaveEditingNote();
        imageCaptionBox.LostFocus += (_, _) => SaveEditingNote();
        createNoteButton.Click += (_, _) => CreateNote();
        deleteNoteButton.Click += (_, _) => DeleteSlip();
        closeBoardButton.Click += (_, _) => CloseBoard();
        ZetlWindowShortcuts.Enable(
            this,
            CloseBoard,
            CloseBoard,
            HandleAdditionalShortcut);
        Closed += (_, _) =>
        {
            store.Changed -= OnStoreChanged;
            DisposeNoteImages();
        };
        store.Changed += OnStoreChanged;

        RefreshProjectCreationFlyout();
        RefreshFromStore(preferActiveProject: true);
    }

    public bool ShiftedLane => shiftedLane;

    // The project shown when the Board closed. The host hands it back to the
    // lane's next Board, which opens there when the lane has nothing active.
    public string? ShownProjectId => ActiveProject?.Id;

    public string? RememberedProjectId { get; set; }

    private ZetlProject? ActiveProject => projectBox.SelectedItem as ZetlProject;

    private ZetlBucket? ActiveBucket => (bucketList.SelectedItem as BucketDisplayItem)?.Bucket;

    private ZetlSlip? ActiveNote => (noteList.SelectedItem as BoardNoteItem)?.Slip;

    public void ShowActiveProject()
    {
        RefreshFromStore(preferActiveProject: true);
    }

    private void OnStoreChanged(object? sender, EventArgs args)
    {
        Dispatcher.UIThread.Post(() => RefreshFromStore());
    }

    private void RefreshFromStore(bool preferActiveProject = false)
    {
        if (store is null)
        {
            return;
        }

        var selectedProjectId = ActiveProject?.Id;
        var selectedBucketId = ActiveBucket?.Id;
        var selectedNoteId = ActiveNote?.Id ?? noteDraft.Slip?.Id;
        var activeProjectId = store.GetActiveProject(shiftedLane)?.Id;

        refreshing = true;
        try
        {
            var firstChoiceId = preferActiveProject ? activeProjectId : selectedProjectId;
            var secondChoiceId = preferActiveProject ? selectedProjectId : activeProjectId;
            // With nothing active or selected, a Board reopens on the project this
            // lane's Board last showed; failing that, the Shift Board opens on the
            // Shift Journal rather than the last project written in either lane, and
            // the Main Board lands on the last project touched.
            var chosen = store.State.Projects.FirstOrDefault(
                    project => project.Id == firstChoiceId)
                ?? store.State.Projects.FirstOrDefault(project => project.Id == secondChoiceId)
                ?? store.State.Projects.FirstOrDefault(project => project.Id == RememberedProjectId)
                ?? (shiftedLane ? store.FindLaneJournal(shifted: true) : null)
                ?? store.GetMostRecentlyWrittenProject()
                ?? store.State.Projects.FirstOrDefault();
            // Opening the Board moves a journal from a finished week (or day, or
            // month) to this one, as a capture would. Only on opening: a background
            // refresh must not pull someone away from last week's journal on purpose.
            if (preferActiveProject && chosen is not null)
            {
                chosen = store.CurrentPeriodJournal(chosen, shiftedLane);
            }

            projectBox.ItemsSource = store.State.Projects.ToList();
            projectBox.SelectedItem = chosen;
            RefreshSelectedProject(selectedBucketId, selectedNoteId);
        }
        finally
        {
            refreshing = false;
        }
    }

    private void SelectProject(string projectId)
    {
        refreshing = true;
        try
        {
            projectBox.SelectedItem = store.State.Projects.FirstOrDefault(
                project => project.Id == projectId);
            RefreshSelectedProject();
        }
        finally
        {
            refreshing = false;
        }
    }

    private void RefreshSelectedProject(string? selectedBucketId = null, string? selectedNoteId = null)
    {
        var wasRefreshing = refreshing;
        refreshing = true;
        try
        {
            var project = ActiveProject;
            var hasProject = project is not null;
            projectNameBox.IsEnabled = hasProject;
            saveProjectButton.IsEnabled = hasProject;
            deleteProjectButton.IsEnabled = hasProject;
            openKastnButton.IsEnabled = hasProject && openInKastn is not null;
            exportProjectButton.IsEnabled = hasProject;
            // A non-Active project cannot be lane-active (the invariant): reactivate
            // it first. The status marker and Reactivate button surface that path.
            var isActiveStatus = project is null || ZetlStateRules.IsActiveStatus(project);
            activeProjectBox.IsEnabled = hasProject && isActiveStatus;
            projectStatusText.Text = project is null ? "" : project.Status;
            projectStatusText.IsVisible = hasProject && !isActiveStatus;
            reactivateProjectButton.IsVisible = hasProject && !isActiveStatus;
            addBucketButton.IsEnabled = hasProject;
            projectNameBox.Text = project?.Name ?? "";
            activeProjectBox.IsChecked = project is not null
                && store.GetActiveProject(shiftedLane)?.Id == project.Id;

            if (project is null)
            {
                bucketList.ItemsSource = Array.Empty<BucketDisplayItem>();
                ClearBucketAndNoteControls();
                return;
            }

            var bucketItems = store.GetBucketDisplayItems(project);
            bucketList.ItemsSource = bucketItems;
            bucketList.SelectedItem = bucketItems.FirstOrDefault(
                    item => item.Bucket.Id == selectedBucketId)
                ?? bucketItems.FirstOrDefault(item => item.Bucket.Id == project.ActiveBucketId)
                ?? bucketItems.FirstOrDefault();
            RefreshSelectedBucket(selectedNoteId);
        }
        finally
        {
            refreshing = wasRefreshing;
        }
    }

    private void RefreshSelectedBucket(string? selectedNoteId = null)
    {
        var wasRefreshing = refreshing;
        refreshing = true;
        try
        {
            var bucket = ActiveBucket;
            if (bucket is null)
            {
                ClearBucketAndNoteControls();
                return;
            }

            var isScratch = ZetlStateRules.IsScratchBucket(bucket);
            bucketNameBox.Text = bucket.Name;
            bucketNameBox.IsEnabled = !isScratch;
            saveBucketButton.IsEnabled = !isScratch;
            deleteBucketButton.IsEnabled = !isScratch;
            bucketSettingsButton.IsEnabled = true;
            bucketKindBox.IsEnabled = true;
            bucketKindBox.SelectedItem = ZetlStateRules.IsReplayBucket(bucket)
                ? "Replay"
                : "Standard";
            createNoteButton.IsEnabled = true;

            DisposeNoteImages();
            noteItems = bucket.Slips
                .Select(slip => CreateNoteItem(ActiveProject!, slip))
                .ToList();
            noteList.ItemsSource = noteItems;
            // While composing a new note the draft isn't in the list yet; keep
            // the list unselected so a background refresh doesn't yank focus onto
            // an existing note mid-typing.
            noteList.SelectedItem = composingBucket is not null
                ? null
                : noteItems.FirstOrDefault(item => item.Slip.Id == selectedNoteId)
                    ?? noteItems.LastOrDefault();
            RefreshSelectedNote();
        }
        finally
        {
            refreshing = wasRefreshing;
        }
    }

    private void ClearBucketAndNoteControls()
    {
        bucketNameBox.Text = "";
        bucketNameBox.IsEnabled = false;
        saveBucketButton.IsEnabled = false;
        deleteBucketButton.IsEnabled = false;
        bucketSettingsButton.IsEnabled = false;
        bucketKindBox.SelectedItem = "Standard";
        bucketKindBox.IsEnabled = false;
        createNoteButton.IsEnabled = false;
        deleteNoteButton.IsEnabled = false;
        DisposeNoteImages();
        noteList.ItemsSource = Array.Empty<BoardNoteItem>();
        composingBucket = null;
        noteDraft.Load(null);
        noteEditor.Text = "";
        imageCaptionBox.Text = "";
        noteEditor.IsEnabled = false;
        noteEditor.IsVisible = true;
        imagePreviewPanel.IsVisible = false;
    }

    private void RefreshSelectedNote()
    {
        // A new-note draft lives only in the editor and isn't in the list, so a
        // refresh (including the background store-changed refresh) must leave the
        // editor untouched; otherwise the in-progress draft would be wiped.
        if (composingBucket is not null)
        {
            return;
        }

        // Preserve unsaved edits across a background refresh (e.g. the activity
        // log flushing while you type): if the same note is still selected and the
        // editor holds typing not yet written, keep it. Anything else reloads,
        // including a newer text another writer (Kastn) saved meanwhile.
        if (noteDraft.Slip is { IsImage: false } shown
            && ActiveNote?.Id == shown.Id
            && noteDraft.IsEdited(noteEditor.Text))
        {
            return;
        }

        noteDraft.Load(ActiveNote);
        var editingNote = noteDraft.Slip;
        noteEditor.Text = editingNote?.Text ?? "";
        var isImage = editingNote?.IsImage == true;
        imageCaptionBox.Text = isImage ? editingNote?.Text ?? "" : "";
        noteEditor.IsVisible = !isImage;
        noteEditor.IsEnabled = editingNote is not null && !isImage;
        imagePreviewPanel.IsVisible = isImage;
        SetSelectedImagePreview(isImage && ActiveProject is { } project
            ? store.ReadImageAsset(project, editingNote!)
            : null);
        deleteNoteButton.IsEnabled = editingNote is not null;
    }

    // Commits the editor's contents: a new-note draft is added to the store only
    // when it has non-blank text (so the Board never persists empty notes), and
    // an existing note is updated only when it was edited here.
    private void SaveEditingNote()
    {
        if (refreshing)
        {
            return;
        }

        var text = noteEditor.Text?.Trim() ?? "";

        if (composingBucket is { } draftBucket)
        {
            composingBucket = null;
            if (text.Length > 0)
            {
                store.AddSlip(draftBucket, text, "manual");
            }

            return;
        }

        // Only typing done in the Board is written back. Text the editor merely
        // loaded is never saved, so a newer save from Kastn is not overwritten.
        if (noteDraft.Slip is not { } note
            || noteDraft.TextToSave(note.IsImage ? imageCaptionBox.Text : noteEditor.Text) is not { } changed)
        {
            return;
        }

        store.UpdateSlip(note, changed);
        noteDraft.Load(note);
    }

    private async Task AddProjectAsync()
    {
        var form = new ProjectSetupWindow(store.Defaults.ProjectBuckets);
        await ShowChildDialogAsync(form);
        if (form.Saved)
        {
            var project = store.CreateProject(
                form.ProjectName,
                form.BucketNames,
                form.ActiveBucketName,
                shiftedLane);
            SelectProject(project.Id);
        }
    }

    private void RefreshProjectCreationFlyout()
    {
        var items = new List<Control>();
        var blankProjectItem = new MenuItem { Header = "Blank Project" };
        blankProjectItem.Click += async (_, _) => await AddProjectAsync();
        items.Add(blankProjectItem);
        items.Add(new Separator());

        var templates = templateStore.LoadAll();
        if (templates.Count == 0)
        {
            items.Add(new MenuItem
            {
                Header = "No templates available",
                IsEnabled = false
            });
        }
        else
        {
            foreach (var template in templates)
            {
                var item = new MenuItem
                {
                    Header = template.Name,
                    IsEnabled = createProjectFromTemplate is not null
                };
                item.Click += async (_, _) => await StartProjectFromTemplateAsync(template);
                items.Add(item);
            }
        }

        newProjectButton.Flyout = new MenuFlyout { ItemsSource = items };
    }

    private async Task StartProjectFromTemplateAsync(ZetlTemplateDocument template)
    {
        var prompt = new TextPromptWindow(
            $"Start from {template.Name}",
            "Project name",
            $"{DateTime.Now:yyyy-MM-dd} {template.Name}",
            "Start Project");
        await ShowChildDialogAsync(prompt);
        if (!prompt.Saved)
        {
            return;
        }

        var project = createProjectFromTemplate?.Invoke(template, prompt.Value, shiftedLane);
        if (project is not null)
        {
            SelectProject(project.Id);
        }
    }

    private async Task DeleteProjectAsync()
    {
        if (ActiveProject is not { } project)
        {
            return;
        }

        if (await ConfirmAsync($"Delete project '{project.Name}'?"))
        {
            store.DeleteProject(project.Id);
        }
    }

    private async Task ExportProjectAsync()
    {
        if (ActiveProject is not { } project)
        {
            return;
        }

        SaveEditingNote();
        await ShowChildDialogAsync(new ProjectExportWindow(
            project,
            store.GetProjectAssets(project)));
    }

    private void SaveProjectName()
    {
        if (ActiveProject is { } project
            && !string.IsNullOrWhiteSpace(projectNameBox.Text))
        {
            store.UpdateProjectName(project, projectNameBox.Text, shiftedLane);
        }
    }

    private async Task AddBucketAsync()
    {
        if (ActiveProject is not { } project)
        {
            return;
        }

        var prompt = new TextPromptWindow("New Bucket", "Bucket name");
        await ShowChildDialogAsync(prompt);
        if (prompt.Saved)
        {
            store.AddBucket(project, prompt.Value);
        }
    }

    private void SaveBucketName()
    {
        if (ActiveBucket is { } bucket
            && !string.IsNullOrWhiteSpace(bucketNameBox.Text)
            && !store.UpdateBucketName(bucket, bucketNameBox.Text))
        {
            bucketNameBox.Text = bucket.Name;
        }
    }

    private async Task ShowBucketSettingsAsync()
    {
        if (ActiveBucket is not { } bucket)
        {
            return;
        }

        var form = new BucketSettingsWindow(bucket, store);
        await ShowChildDialogAsync(form);
        if (form.Saved)
        {
            store.UpdateBucketSettings(
                bucket,
                form.BucketName,
                form.DefaultKind,
                form.DefaultCompileMode,
                form.DefaultStartingText,
                form.DefaultTsvRowLength);
        }

        Dispatcher.UIThread.Post(() => noteEditor.Focus());
    }

    private async Task DeleteBucketAsync()
    {
        if (ActiveProject is not { } project || ActiveBucket is not { } bucket)
        {
            return;
        }

        if (await ConfirmAsync($"Delete bucket '{bucket.Name}'?"))
        {
            store.DeleteBucket(project, bucket.Id);
        }
    }

    private void CreateNote()
    {
        if (ActiveBucket is not { } bucket)
        {
            return;
        }

        // Commit whatever is in the editor first: an existing-note edit, or a
        // previous new-note draft. A blank draft commits to nothing, so pressing
        // New Note repeatedly never piles up empty notes.
        SaveEditingNote();

        refreshing = true;
        try
        {
            composingBucket = bucket;
            noteDraft.Load(null);
            noteList.SelectedItem = null;
            noteEditor.Text = "";
            noteEditor.IsVisible = true;
            noteEditor.IsEnabled = true;
            imagePreviewPanel.IsVisible = false;
            imageCaptionBox.Text = "";
            SetSelectedImagePreview(null);
            deleteNoteButton.IsEnabled = false;
        }
        finally
        {
            refreshing = false;
        }

        Dispatcher.UIThread.Post(() => noteEditor.Focus());
    }

    private void DeleteSlip()
    {
        if (ActiveBucket is { } bucket && ActiveNote is { } note)
        {
            noteDraft.Load(null);
            store.DeleteSlip(bucket, note.Id);
        }
    }

    private BoardNoteItem CreateNoteItem(ZetlProject project, ZetlSlip note)
    {
        Bitmap? thumbnail = null;
        if (note.IsImage && store.ReadImageAsset(project, note) is { } bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                thumbnail = Bitmap.DecodeToWidth(stream, 144);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                thumbnail = null;
            }
        }

        return new BoardNoteItem(note, thumbnail);
    }

    private void SetSelectedImagePreview(byte[]? bytes)
    {
        selectedImagePreview?.Dispose();
        selectedImagePreview = null;
        imagePreview.Source = null;
        if (bytes is null)
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            selectedImagePreview = Bitmap.DecodeToWidth(stream, 900);
            imagePreview.Source = selectedImagePreview;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            selectedImagePreview = null;
        }
    }

    private void DisposeNoteImages()
    {
        foreach (var item in noteItems)
        {
            item.Thumbnail?.Dispose();
        }

        noteItems = [];
        selectedImagePreview?.Dispose();
        selectedImagePreview = null;
        imagePreview.Source = null;
    }

    private void ToggleActiveProjectFromControl()
    {
        if (refreshing || ActiveProject is not { } project)
        {
            return;
        }

        if (activeProjectBox.IsChecked == true)
        {
            store.SetActiveProject(project.Id, shiftedLane);
        }
        else if (store.GetActiveProject(shiftedLane)?.Id == project.Id)
        {
            store.ClearActiveProject(shiftedLane);
        }
    }

    private void HandleAdditionalShortcut(KeyEventArgs args)
    {
        if (args.Key == Key.A
            && args.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            args.Handled = true;
            activeProjectBox.IsChecked = activeProjectBox.IsChecked != true;
        }
    }

    // childDialogOpen suppresses the click-away while an owned dialog (confirm,
    // bucket settings, project/bucket prompts) holds focus.
    protected override bool IsDismissSuppressed => childDialogOpen;

    protected override void OnClickAwayDismiss()
    {
        CloseBoard();
    }

    protected override void OnPopupClosing()
    {
        SaveEditingNote();
    }

    private void CloseBoard()
    {
        SaveEditingNote();
        Close();
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        var form = new ConfirmWindow(message);
        childDialogOpen = true;
        try
        {
            return await form.ShowDialog<bool>(this);
        }
        finally
        {
            childDialogOpen = false;
        }
    }

    private async Task ShowChildDialogAsync(Window form)
    {
        childDialogOpen = true;
        try
        {
            await form.ShowDialog(this);
        }
        finally
        {
            childDialogOpen = false;
        }
    }
}

internal sealed record BoardNoteItem(ZetlSlip Slip, Bitmap? Thumbnail)
{
    public string DisplayText => Slip.DisplayText;
    public bool IsImage => Slip.IsImage;
    public bool HasCaptureOrigin => Slip.HasCaptureOrigin;
    public string CaptureOriginLabel => Slip.CaptureOriginLabel;
}
