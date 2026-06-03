namespace ZETL;

internal sealed partial class NoteCaptureForm : Form
{
    private readonly ZetlStateStore store;
    private readonly ZetlProject project;
    private readonly bool showStartProjectToggle;
    private readonly bool scratchOnlyUntilProjectStarted;
    private ZetlBucket? lastFullProjectBucket;

    public NoteCaptureForm(
        ZetlStateStore store,
        ZetlProject project,
        ZetlBucket? preferredBucket,
        string text,
        bool showStartProjectToggle = false,
        bool startProjectDefault = true,
        bool scratchOnlyUntilProjectStarted = false)
    {
        this.store = store;
        this.project = project;
        this.showStartProjectToggle = showStartProjectToggle;
        this.scratchOnlyUntilProjectStarted = scratchOnlyUntilProjectStarted;
        lastFullProjectBucket = preferredBucket;

        InitializeComponent();
        ZetlFormShortcuts.EnableCtrlEnterClose(this);
        projectModePanel.Visible = showStartProjectToggle;
        projectNameBox.Text = project.Name;
        startProjectBox.Checked = !showStartProjectToggle || startProjectDefault;
        noteBox.Text = BuildInitialNoteText(text);

        bucketBox.SelectedIndexChanged += (_, _) => UpdateInlineBucketLabel();
        startProjectBox.CheckedChanged += (_, _) =>
        {
            RefreshBuckets(startProjectBox.Checked ? lastFullProjectBucket : store.GetScratchBucket(project));
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

    public string NoteText => noteBox.Text.Trim();

    public bool StartProject => !showStartProjectToggle || startProjectBox.Checked;

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
        if (!StartProject)
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
        var items = StartProject || !scratchOnlyUntilProjectStarted
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
        inlineBucketPanel.Visible = StartProject;
        if (StartProject && bucketBox.SelectedItem is BucketDisplayItem item)
        {
            lastFullProjectBucket = item.Bucket;
        }
    }
}
