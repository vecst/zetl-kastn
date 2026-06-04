namespace ZETL;

internal sealed partial class BoardForm : ZetlPopupForm
{
    private readonly ZetlStateStore store;
    private readonly bool shiftedLane;
    private bool autoHideArmed;
    private bool autoHideOnDeactivate;
    private bool refreshing;
    private bool suppressRestoreOnClose;

    public BoardForm(ZetlStateStore store, bool shiftedLane = false)
    {
        this.store = store;
        this.shiftedLane = shiftedLane;
        InitializeComponent();
        Text = shiftedLane ? "Zetl Board - Shift" : "Zetl Board";
        noteList.DisplayMember = nameof(ZetlNote.Text);

        projectBox.SelectedIndexChanged += (_, _) =>
        {
            if (refreshing || projectBox.SelectedItem is not ZetlProject project)
            {
                return;
            }

            RefreshSelectedProject();
        };
        activeProjectBox.CheckedChanged += (_, _) =>
        {
            if (refreshing || ActiveProject is not { } project)
            {
                return;
            }

            if (activeProjectBox.Checked)
            {
                store.SetActiveProject(project.Id, shiftedLane);
            }
            else if (store.GetActiveProject(shiftedLane)?.Id == project.Id)
            {
                store.ClearActiveProject(shiftedLane);
            }
        };
        bucketList.SelectedIndexChanged += (_, _) =>
        {
            if (refreshing || ActiveProject is null || ActiveBucket is not { } bucket)
            {
                return;
            }

            store.SetActiveBucket(ActiveProject, bucket.Id);
        };
        bucketList.DoubleClick += (_, _) => ShowBucketSettings();
        noteList.SelectedIndexChanged += (_, _) => RefreshSelectedNote();
        popModeBox.CheckedChanged += (_, _) =>
        {
            if (!refreshing && ActiveBucket is { } bucket)
            {
                store.SetBucketPopMode(bucket, popModeBox.Checked);
            }
        };
        bucketKindBox.SelectedIndexChanged += (_, _) =>
        {
            if (!refreshing && ActiveBucket is { } bucket && bucketKindBox.SelectedItem is string kind)
            {
                store.SetBucketKind(bucket, kind);
            }
        };
        newProjectButton.Click += (_, _) => AddProject();
        deleteProjectButton.Click += (_, _) => DeleteProject();
        closeBoardButton.Click += (_, _) => Close();
        saveProjectButton.Click += (_, _) => SaveProjectName();
        addBucketButton.Click += (_, _) => AddBucket();
        deleteBucketButton.Click += (_, _) => DeleteBucket();
        saveBucketButton.Click += (_, _) => SaveBucketName();
        bucketNameBox.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter)
            {
                args.SuppressKeyPress = true;
                SaveBucketName();
            }
        };
        noteEditor.Leave += (_, _) => SaveNote();
        deleteNoteButton.Click += (_, _) => DeleteNote();
        store.Changed += (_, _) => RefreshFromStore();
        Deactivate += (_, _) =>
        {
            BeginInvoke(new Action(HideTransientBoardIfInactive));
        };
        FormClosing += (_, args) =>
        {
            if (args.CloseReason == CloseReason.UserClosing)
            {
                args.Cancel = true;
                Hide();
                if (!suppressRestoreOnClose && RestoreWindowOnClose != IntPtr.Zero)
                {
                    Program.SetForegroundWindow(RestoreWindowOnClose);
                }
            }
        };

        RefreshFromStore();
    }

    public bool ShiftedLane => shiftedLane;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IntPtr RestoreWindowOnClose { get; set; }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AutoHideOnDeactivate
    {
        get => autoHideOnDeactivate;
        set
        {
            autoHideOnDeactivate = value;
            autoHideArmed = false;
            if (value)
            {
                ArmAutoHide();
            }
        }
    }

    private ZetlProject? ActiveProject => projectBox.SelectedItem as ZetlProject;

    private ZetlBucket? ActiveBucket => (bucketList.SelectedItem as BucketDisplayItem)?.Bucket;

    private ZetlNote? ActiveNote => noteList.SelectedItem as ZetlNote;

    // Re-select the active project. Called when the board is (re)opened so a
    // reused board jumps to the lane's active project instead of keeping the
    // project that happened to be selected last time.
    public void ShowActiveProject()
    {
        RefreshFromStore(preferActiveProject: true);
    }

    private void RefreshFromStore(bool preferActiveProject = false)
    {
        refreshing = true;
        try
        {
            var selectedProjectId = (projectBox.SelectedItem as ZetlProject)?.Id;
            var activeProjectId = store.GetActiveProject(shiftedLane)?.Id;
            // While the board is open, preserve the user's current selection
            // across store updates; on open, prefer the active project.
            var firstChoiceId = preferActiveProject ? activeProjectId : selectedProjectId;
            var secondChoiceId = preferActiveProject ? selectedProjectId : activeProjectId;
            projectBox.Items.Clear();
            projectBox.Items.AddRange(store.State.Projects.Cast<object>().ToArray());
            projectBox.SelectedItem = store.State.Projects.FirstOrDefault(project => project.Id == firstChoiceId)
                ?? store.State.Projects.FirstOrDefault(project => project.Id == secondChoiceId)
                ?? store.State.Projects.FirstOrDefault();
            RefreshSelectedProject();
        }
        finally
        {
            refreshing = false;
        }
    }

    private void RefreshSelectedProject()
    {
        var wasRefreshing = refreshing;
        refreshing = true;
        try
        {
            bucketList.Items.Clear();
            var project = ActiveProject;
            if (project is null)
            {
                projectNameBox.Text = "";
                noteList.Items.Clear();
                noteEditor.Text = "";
                activeProjectBox.Checked = false;
                bucketNameBox.Text = "";
                bucketNameBox.Enabled = false;
                saveBucketButton.Enabled = false;
                bucketKindBox.SelectedItem = "Standard";
                bucketKindBox.Enabled = false;
                popModeBox.Checked = false;
                popModeBox.Enabled = false;
                return;
            }

            projectNameBox.Text = project.Name;
            activeProjectBox.Checked = store.GetActiveProject(shiftedLane)?.Id == project.Id;
            var bucketItems = store.GetBucketDisplayItems(project);
            bucketList.Items.AddRange(bucketItems.Cast<object>().ToArray());
            bucketList.SelectedItem = bucketItems.FirstOrDefault(item => item.Bucket.Id == project.ActiveBucketId)
                ?? bucketItems.FirstOrDefault();
            RefreshSelectedBucket();
        }
        finally
        {
            refreshing = wasRefreshing;
        }
    }

    private void RefreshSelectedBucket()
    {
        var wasRefreshing = refreshing;
        refreshing = true;
        try
        {
            noteList.Items.Clear();
            var bucket = ActiveBucket;
            if (bucket is null)
            {
                noteEditor.Text = "";
                bucketNameBox.Text = "";
                bucketNameBox.Enabled = false;
                saveBucketButton.Enabled = false;
                deleteBucketButton.Enabled = false;
                bucketKindBox.SelectedItem = "Standard";
                bucketKindBox.Enabled = false;
                popModeBox.Checked = false;
                popModeBox.Enabled = false;
                return;
            }

            // Scratch is always present and is the quick-note default, so it
            // cannot be renamed or deleted (its other settings stay editable).
            var isScratch = ZetlStateStore.IsScratchBucket(bucket);
            bucketNameBox.Text = bucket.Name;
            bucketNameBox.Enabled = !isScratch;
            saveBucketButton.Enabled = !isScratch;
            deleteBucketButton.Enabled = !isScratch;
            bucketKindBox.Enabled = true;
            bucketKindBox.SelectedItem = ZetlStateStore.IsFifoBucket(bucket) ? "Replay" : "Standard";
            popModeBox.Checked = bucket.PopMode;
            popModeBox.Enabled = !ZetlStateStore.IsFifoBucket(bucket);
            noteList.Items.AddRange(bucket.Notes.Cast<object>().ToArray());
            noteList.SelectedItem = bucket.Notes.LastOrDefault();
            RefreshSelectedNote();
        }
        finally
        {
            refreshing = wasRefreshing;
        }
    }

    private void RefreshSelectedNote()
    {
        noteEditor.Text = ActiveNote?.Text ?? "";
    }

    private void AddProject()
    {
        using var form = new ProjectSetupForm(store.Defaults.ProjectBuckets);
        if (ShowOwnedDialog(form) == DialogResult.OK)
        {
            store.CreateProject(form.ProjectName, form.BucketNames, form.ActiveBucketName, shiftedLane);
        }
    }

    private void DeleteProject()
    {
        if (ActiveProject is not { } project)
        {
            return;
        }

        if (ShowOwnedMessageBox($"Delete project '{project.Name}'?", "Zetl", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            store.DeleteProject(project.Id);
        }
    }

    private void SaveProjectName()
    {
        if (ActiveProject is null || string.IsNullOrWhiteSpace(projectNameBox.Text))
        {
            return;
        }

        store.UpdateProjectName(ActiveProject, projectNameBox.Text, shiftedLane);
    }

    private void AddBucket()
    {
        if (ActiveProject is not { } project)
        {
            return;
        }

        using var prompt = new TextPromptForm("New Bucket", "Bucket name");
        if (ShowOwnedDialog(prompt) == DialogResult.OK)
        {
            store.AddBucket(project, prompt.Value);
        }
    }

    private void SaveBucketName()
    {
        if (ActiveBucket is null || string.IsNullOrWhiteSpace(bucketNameBox.Text))
        {
            return;
        }

        store.UpdateBucketName(ActiveBucket, bucketNameBox.Text);
    }

    private void ShowBucketSettings()
    {
        if (ActiveProject is null || ActiveBucket is not { } bucket)
        {
            return;
        }

        using var form = new BucketSettingsForm(bucket, store);
        if (ShowOwnedDialog(form) != DialogResult.OK)
        {
            return;
        }

        store.UpdateBucketSettings(
            bucket,
            form.BucketName,
            form.DefaultKind,
            form.DefaultCompileMode,
            form.DefaultStartingText,
            form.DefaultTsvRowLength);
    }

    private void DeleteBucket()
    {
        if (ActiveProject is not { } project || ActiveBucket is not { } bucket)
        {
            return;
        }

        if (ShowOwnedMessageBox($"Delete bucket '{bucket.Name}'?", "Zetl", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            store.DeleteBucket(project, bucket.Id);
        }
    }

    private void SaveNote()
    {
        if (ActiveNote is null || string.IsNullOrWhiteSpace(noteEditor.Text))
        {
            return;
        }

        if (string.Equals(ActiveNote.Text, noteEditor.Text.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        store.UpdateNote(ActiveNote, noteEditor.Text);
    }

    private void DeleteNote()
    {
        if (ActiveBucket is null || ActiveNote is null)
        {
            return;
        }

        store.DeleteNote(ActiveBucket, ActiveNote.Id);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Enter))
        {
            SaveNote();
            Close();
            return true;
        }

        if (keyData == (Keys.Alt | Keys.A))
        {
            ToggleActiveProject();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ToggleActiveProject()
    {
        if (ActiveProject is null)
        {
            return;
        }

        activeProjectBox.Checked = !activeProjectBox.Checked;
    }

    private DialogResult ShowOwnedDialog(Form form)
    {
        return ZetlDialogPlacement.ShowForegroundDialog(form, this);
    }

    private DialogResult ShowOwnedMessageBox(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        return MessageBox.Show(this, text, caption, buttons, icon);
    }

    private void HideTransientBoardIfInactive()
    {
        if (!AutoHideOnDeactivate || !autoHideArmed || IsDisposed || !Visible)
        {
            return;
        }

        // If focus settled on another window of our own process (a child dialog
        // or message box opened from the board), keep the board open. Only hide
        // when the user moved to another app.
        var foreground = Program.GetForegroundWindow();
        Program.GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        if (foregroundProcessId == (uint)Environment.ProcessId)
        {
            return;
        }

        suppressRestoreOnClose = true;
        try
        {
            Close();
        }
        finally
        {
            suppressRestoreOnClose = false;
        }
    }

    private void ArmAutoHide()
    {
        var armTimer = new System.Windows.Forms.Timer { Interval = 150 };
        armTimer.Tick += (_, _) =>
        {
            armTimer.Stop();
            armTimer.Dispose();
            if (!IsDisposed && Visible && autoHideOnDeactivate)
            {
                autoHideArmed = true;
            }
        };
        armTimer.Start();
    }
}
