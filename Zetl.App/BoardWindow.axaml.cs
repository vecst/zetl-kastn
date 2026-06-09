using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ZETL;

internal partial class BoardWindow : ZetlPopupWindow
{
    private readonly ZetlStateStore store = null!;
    private readonly bool shiftedLane;
    private bool refreshing;
    private bool childDialogOpen;
    private ZetlNote? editingNote;

    public BoardWindow()
    {
        InitializeComponent();
    }

    internal BoardWindow(ZetlStateStore store, bool shiftedLane = false)
    {
        this.store = store;
        this.shiftedLane = shiftedLane;
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
        popModeBox.IsCheckedChanged += (_, _) =>
        {
            if (!refreshing && ActiveBucket is { } bucket)
            {
                store.SetBucketPopMode(bucket, popModeBox.IsChecked == true);
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
        deleteProjectButton.Click += async (_, _) => await DeleteProjectAsync();
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
        createNoteButton.Click += (_, _) => CreateNote();
        deleteNoteButton.Click += (_, _) => DeleteNote();
        closeBoardButton.Click += (_, _) => CloseBoard();
        ZetlWindowShortcuts.Enable(
            this,
            CloseBoard,
            CloseBoard,
            HandleAdditionalShortcut);
        Closed += (_, _) => store.Changed -= OnStoreChanged;
        store.Changed += OnStoreChanged;

        RefreshFromStore(preferActiveProject: true);
    }

    public bool ShiftedLane => shiftedLane;

    private ZetlProject? ActiveProject => projectBox.SelectedItem as ZetlProject;

    private ZetlBucket? ActiveBucket => (bucketList.SelectedItem as BucketDisplayItem)?.Bucket;

    private ZetlNote? ActiveNote => noteList.SelectedItem as ZetlNote;

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
        var selectedNoteId = ActiveNote?.Id ?? editingNote?.Id;
        var activeProjectId = store.GetActiveProject(shiftedLane)?.Id;

        refreshing = true;
        try
        {
            projectBox.ItemsSource = store.State.Projects.ToList();
            var firstChoiceId = preferActiveProject ? activeProjectId : selectedProjectId;
            var secondChoiceId = preferActiveProject ? selectedProjectId : activeProjectId;
            projectBox.SelectedItem = store.State.Projects.FirstOrDefault(
                    project => project.Id == firstChoiceId)
                ?? store.State.Projects.FirstOrDefault(project => project.Id == secondChoiceId)
                ?? store.State.Projects.FirstOrDefault();
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
            activeProjectBox.IsEnabled = hasProject;
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

            var isScratch = ZetlStateStore.IsScratchBucket(bucket);
            bucketNameBox.Text = bucket.Name;
            bucketNameBox.IsEnabled = !isScratch;
            saveBucketButton.IsEnabled = !isScratch;
            deleteBucketButton.IsEnabled = !isScratch;
            bucketSettingsButton.IsEnabled = true;
            bucketKindBox.IsEnabled = true;
            bucketKindBox.SelectedItem = ZetlStateStore.IsFifoBucket(bucket)
                ? "Replay"
                : "Standard";
            popModeBox.IsChecked = bucket.PopMode;
            popModeBox.IsEnabled = !ZetlStateStore.IsFifoBucket(bucket);
            createNoteButton.IsEnabled = true;

            noteList.ItemsSource = bucket.Notes.ToList();
            noteList.SelectedItem = bucket.Notes.FirstOrDefault(note => note.Id == selectedNoteId)
                ?? bucket.Notes.LastOrDefault();
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
        popModeBox.IsChecked = false;
        popModeBox.IsEnabled = false;
        createNoteButton.IsEnabled = false;
        deleteNoteButton.IsEnabled = false;
        noteList.ItemsSource = Array.Empty<ZetlNote>();
        editingNote = null;
        noteEditor.Text = "";
        noteEditor.IsEnabled = false;
    }

    private void RefreshSelectedNote()
    {
        editingNote = ActiveNote;
        noteEditor.Text = editingNote?.Text ?? "";
        noteEditor.IsEnabled = editingNote is not null;
        deleteNoteButton.IsEnabled = editingNote is not null;
    }

    private void SaveEditingNote()
    {
        if (refreshing
            || editingNote is null
            || string.IsNullOrWhiteSpace(noteEditor.Text))
        {
            return;
        }

        var text = noteEditor.Text.Trim();
        if (!string.Equals(editingNote.Text, text, StringComparison.Ordinal))
        {
            store.UpdateNote(editingNote, text);
        }
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
            && !string.IsNullOrWhiteSpace(bucketNameBox.Text))
        {
            store.UpdateBucketName(bucket, bucketNameBox.Text);
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

        var note = store.AddNote(bucket, "", "manual");
        Dispatcher.UIThread.Post(() =>
        {
            noteList.SelectedItem = note;
            noteEditor.Focus();
        });
    }

    private void DeleteNote()
    {
        if (ActiveBucket is { } bucket && ActiveNote is { } note)
        {
            editingNote = null;
            store.DeleteNote(bucket, note.Id);
        }
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
