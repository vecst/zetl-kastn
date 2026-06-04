namespace ZETL;

internal sealed partial class NoteCaptureForm : ZetlPopupForm
{
    private readonly ZetlStateStore store;
    private readonly ZetlProject project;
    private readonly bool showStartProjectToggle;
    private readonly bool scratchOnlyUntilProjectStarted;
    private readonly bool createNewProjectMode;
    private ZetlBucket? lastFullProjectBucket;

    public NoteCaptureForm(
        ZetlStateStore store,
        ZetlProject project,
        ZetlBucket? preferredBucket,
        string text,
        bool showStartProjectToggle = false,
        bool startProjectDefault = true,
        bool scratchOnlyUntilProjectStarted = false,
        bool createNewProjectMode = false,
        string? projectToggleText = null,
        string? projectNameDefault = null)
    {
        this.store = store;
        this.project = project;
        this.showStartProjectToggle = showStartProjectToggle;
        this.scratchOnlyUntilProjectStarted = scratchOnlyUntilProjectStarted;
        this.createNewProjectMode = createNewProjectMode;
        lastFullProjectBucket = preferredBucket;

        InitializeComponent();
        projectModePanel.Visible = showStartProjectToggle;
        startProjectBox.Text = projectToggleText ?? "Start project";
        projectNameBox.Text = projectNameDefault ?? project.Name;
        startProjectBox.Checked = !showStartProjectToggle || startProjectDefault;
        noteBox.Text = BuildInitialNoteText(text);

        // Non-modal forms do not auto-close when a button sets DialogResult, so
        // close explicitly. The DialogResult is already set by the time Click
        // fires.
        saveButton.Click += (_, _) => Close();
        cancelButton.Click += (_, _) => Close();
        bucketBox.SelectedIndexChanged += (_, _) => UpdateInlineBucketLabel();
        startProjectBox.CheckedChanged += (_, _) =>
        {
            var selectedBucket = createNewProjectMode
                ? startProjectBox.Checked ? null : lastFullProjectBucket
                : startProjectBox.Checked ? lastFullProjectBucket : store.GetScratchBucket(project);
            RefreshBuckets(selectedBucket);
            UpdateProjectModeControls();
        };
        inlineCreateButton.Click += (_, _) => CreateInlineBucket();
        inlineBucketNameBox.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter)
            {
                args.SuppressKeyPress = true;
                CreateInlineBucket();
            }
        };
        Shown += (_, _) =>
        {
            UpdateInlineBucketLabel();
            FocusNoteBox();
            BeginInvoke(FocusNoteBox);
        };

        RefreshBuckets(preferredBucket);
        UpdateProjectModeControls();
    }

    public ZetlBucket SelectedBucket => ((BucketDisplayItem)bucketBox.SelectedItem!).Bucket;

    public string SelectedBucketName => SelectedBucket.Name;

    public string NoteText => noteBox.Text.Trim();

    public bool StartProject => !showStartProjectToggle || startProjectBox.Checked;

    public bool CreateNewProject => showStartProjectToggle && createNewProjectMode && startProjectBox.Checked;

    public string ProjectName => projectNameBox.Text.Trim();

    private static string BuildInitialNoteText(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? "" : $"{trimmed} ";
    }

    private void FocusNoteBox()
    {
        ActiveControl = noteBox;
        noteBox.Focus();
        Program.SetFocus(noteBox.Handle);
        noteBox.SelectionStart = noteBox.TextLength;
        noteBox.SelectionLength = 0;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Alt | Keys.B))
        {
            if (inlineBucketPanel.Visible)
            {
                FocusInlineBucketName();
            }

            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void FocusInlineBucketName()
    {
        UpdateInlineBucketLabel();
        inlineBucketNameBox.Focus();
        inlineBucketNameBox.SelectAll();
    }

    private void CreateInlineBucket()
    {
        if (!StartProject || CreateNewProject)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(inlineBucketNameBox.Text))
        {
            inlineBucketNameBox.Focus();
            return;
        }

        var selectedBucket = SelectedBucket;
        var bucket = store.AddBucket(
            project,
            inlineBucketNameBox.Text,
            inlineInsideCurrentBox.Checked ? selectedBucket.Id : null);
        lastFullProjectBucket = bucket;
        RefreshBuckets(bucket);
        inlineBucketNameBox.Clear();
        FocusNoteBox();
    }

    private void UpdateInlineBucketLabel()
    {
        if (bucketBox.SelectedItem is BucketDisplayItem item)
        {
            inlineInsideCurrentBox.Text = $"Inside {item.Bucket.Name}";
        }
    }

    private void RefreshBuckets(ZetlBucket? selectedBucket)
    {
        bucketBox.Items.Clear();
        var items = CreateNewProject
            ? GetNewProjectBucketItems()
            : StartProject || !scratchOnlyUntilProjectStarted
                ? store.GetBucketDisplayItems(project)
                : store.GetBucketDisplayItems(project)
                .Where(item => string.Equals(item.Bucket.Name, "Scratch", StringComparison.OrdinalIgnoreCase))
                .ToList();
        bucketBox.Items.AddRange(items.Cast<object>().ToArray());
        bucketBox.SelectedItem = items.FirstOrDefault(item => item.Bucket.Id == selectedBucket?.Id)
            ?? items.FirstOrDefault();
    }

    private void UpdateProjectModeControls()
    {
        projectNameBox.Enabled = StartProject;
        inlineBucketPanel.Visible = StartProject && !CreateNewProject;
        if (StartProject && !CreateNewProject && bucketBox.SelectedItem is BucketDisplayItem item)
        {
            lastFullProjectBucket = item.Bucket;
        }
    }

    private IReadOnlyList<BucketDisplayItem> GetNewProjectBucketItems()
    {
        var bucketNames = store.Defaults.ProjectBuckets
            .Append("Scratch")
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (bucketNames.Count == 0)
        {
            bucketNames.Add("Inbox");
            bucketNames.Add("Scratch");
        }

        return bucketNames
            .Select(name =>
            {
                var bucket = new ZetlBucket { Id = name, Name = name };
                return new BucketDisplayItem(bucket, name);
            })
            .ToList();
    }
}
