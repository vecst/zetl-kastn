namespace ZETL;

internal sealed partial class CompileForm : ZetlPopupForm
{
    private readonly ZetlStateStore store;
    private ZetlProject sourceProject;
    private IReadOnlyList<ZetlBucket>? sourceScope;
    private bool suppressTreeEvents;

    public CompileForm(ZetlStateStore store, ZetlProject project, IReadOnlyList<ZetlBucket>? bucketScope = null)
    {
        this.store = store;
        sourceProject = project;
        sourceScope = bucketScope;

        InitializeComponent();

        PopulateProjectSelectors();
        RebuildNoteTree();
        RefreshDestinationBuckets();
        ApplyBucketCompileDefaults();

        // Wire selectors after populating so setting the initial selection does
        // not fire the change handlers.
        sourceProjectBox.SelectedIndexChanged += (_, _) => OnSourceProjectChanged();
        destinationProjectBox.SelectedIndexChanged += (_, _) => RefreshDestinationBuckets(resetSelection: true);
        noteTree.AfterCheck += OnNoteTreeAfterCheck;
        selectAllButton.Click += (_, _) => SetAllChecked(true);
        selectNoneButton.Click += (_, _) => SetAllChecked(false);
        sessionOnlyCheck.CheckedChanged += (_, _) => RebuildNoteTree();
        compileModeBox.SelectedIndexChanged += (_, _) =>
        {
            UpdateCompileModeControls();
            RefreshPreview();
        };
        tsvRowLengthBox.ValueChanged += (_, _) => RefreshPreview();
        copyButton.Click += (_, _) => Complete(pasteNow: false, saveToBucket: false);
        pasteButton.Click += (_, _) => Complete(pasteNow: true, saveToBucket: false);
        pastePlainButton.Click += (_, _) => Complete(pasteNow: true, saveToBucket: false, unformatted: true);
        pasteLastButton.Click += (_, _) => CompleteLastItem();
        saveBucketButton.Click += (_, _) => Complete(pasteNow: false, saveToBucket: true);
        cancelButton.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        UpdateCompileModeControls();
        RefreshPreview();
    }

    public string CompiledText { get; private set; } = "";

    public bool PasteNow { get; private set; }

    public bool SaveToBucket { get; private set; }

    // The project whose notes are being compiled. The dialog never changes which
    // project is active in the app; it only reads from the one you select here.
    public ZetlProject SourceProject => sourceProject;

    // Where a "Save to Bucket" compile should land. May differ from the source.
    public ZetlProject DestinationProject => destinationProjectBox.SelectedItem as ZetlProject ?? sourceProject;

    public string DestinationBucketName => destinationBucketBox.Text.Trim();

    private IReadOnlyList<NoteDisplayItem> SelectedNotes => noteTree.Nodes.Cast<TreeNode>()
        .SelectMany(bucketNode => bucketNode.Nodes.Cast<TreeNode>())
        .Where(noteNode => noteNode.Checked && noteNode.Tag is NoteDisplayItem)
        .Select(noteNode => (NoteDisplayItem)noteNode.Tag!)
        .ToList();

    private bool HasSelectedNotes => SelectedNotes.Count > 0;

    private string SelectedCompileMode => compileModeBox.SelectedItem as string ?? "Formatted";

    private int TsvRowLength => Math.Max(1, (int)tsvRowLengthBox.Value);

    private ZetlBucket? DefaultCompileBucket => sourceScope is { Count: 1 }
        ? sourceScope[0]
        : sourceProject.Buckets.FirstOrDefault(bucket => bucket.Id == sourceProject.ActiveBucketId)
            ?? sourceProject.Buckets.FirstOrDefault();

    private void PopulateProjectSelectors()
    {
        var projects = store.State.Projects
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Cast<object>()
            .ToArray();
        sourceProjectBox.Items.AddRange(projects);
        destinationProjectBox.Items.AddRange(projects);
        sourceProjectBox.SelectedItem = sourceProject;
        destinationProjectBox.SelectedItem = sourceProject;
    }

    private void OnSourceProjectChanged()
    {
        if (sourceProjectBox.SelectedItem is not ZetlProject selected || selected.Id == sourceProject.Id)
        {
            return;
        }

        sourceProject = selected;
        // Choosing a project explicitly compiles the whole project, dropping any
        // scratch-fallback scope the dialog opened with.
        sourceScope = null;
        RebuildNoteTree();
        ApplyBucketCompileDefaults();
    }

    // Rebuilds the bucket-grouped checklist tree for the current session-scope
    // toggle. Notes in the active bucket (or the whole scope when compiling a
    // scratch fallback) start checked, mirroring the old default selection.
    private void RebuildNoteTree()
    {
        suppressTreeEvents = true;
        noteTree.BeginUpdate();
        noteTree.Nodes.Clear();

        var scoped = sourceScope is not null;
        foreach (var group in store.GetNoteDisplayItems(sourceProject, sourceScope, sessionOnlyCheck.Checked)
            .GroupBy(item => item.Bucket))
        {
            var bucketNode = new TreeNode(group.Key.Name.Trim()) { Tag = group.Key };
            var defaultChecked = scoped || group.Key.Id == sourceProject.ActiveBucketId;
            foreach (var item in group)
            {
                bucketNode.Nodes.Add(new TreeNode(NotePreview(item.Note.Text)) { Tag = item, Checked = defaultChecked });
            }

            bucketNode.Checked = bucketNode.Nodes.Cast<TreeNode>().All(node => node.Checked);
            noteTree.Nodes.Add(bucketNode);
        }

        noteTree.ExpandAll();
        noteTree.EndUpdate();
        suppressTreeEvents = false;
        RefreshPreview();
    }

    private void OnNoteTreeAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (suppressTreeEvents || e.Node is null)
        {
            return;
        }

        suppressTreeEvents = true;
        if (e.Node.Tag is ZetlBucket)
        {
            foreach (TreeNode child in e.Node.Nodes)
            {
                child.Checked = e.Node.Checked;
            }
        }
        else if (e.Node.Parent is { } parent)
        {
            parent.Checked = parent.Nodes.Cast<TreeNode>().All(node => node.Checked);
        }

        suppressTreeEvents = false;
        RefreshPreview();
    }

    private void SetAllChecked(bool value)
    {
        suppressTreeEvents = true;
        foreach (TreeNode bucketNode in noteTree.Nodes)
        {
            bucketNode.Checked = value;
            foreach (TreeNode noteNode in bucketNode.Nodes)
            {
                noteNode.Checked = value;
            }
        }

        suppressTreeEvents = false;
        RefreshPreview();
    }

    private static string NotePreview(string text)
    {
        var preview = text.ReplaceLineEndings(" ").Trim();
        return preview.Length <= 80 ? preview : $"{preview[..77]}...";
    }

    private void RefreshPreview()
    {
        var selected = SelectedNotes;
        if (selected.Count == 0)
        {
            previewBox.Text = "";
            return;
        }

        previewBox.Text = BuildCompiledText(selected);
    }

    private string BuildCompiledText(IReadOnlyList<NoteDisplayItem> selected)
    {
        return SelectedCompileMode switch
        {
            "Plain" => store.CompileUnformattedFromNotes(selected),
            "TSV" => store.CompileTsvFromNotes(sourceProject, selected, TsvRowLength),
            _ => store.CompilePlainTextFromNotes(sourceProject, selected)
        };
    }

    private void UpdateCompileModeControls()
    {
        var tsvSelected = string.Equals(SelectedCompileMode, "TSV", StringComparison.OrdinalIgnoreCase);
        tsvRowLengthLabel.Enabled = tsvSelected;
        tsvRowLengthBox.Enabled = tsvSelected;
    }

    private void ApplyBucketCompileDefaults()
    {
        var bucket = DefaultCompileBucket;
        compileModeBox.SelectedItem = bucket?.DefaultCompileMode is "Plain" or "TSV"
            ? bucket.DefaultCompileMode
            : "Formatted";
        if (bucket is not null)
        {
            tsvRowLengthBox.Value = Math.Min(tsvRowLengthBox.Maximum, Math.Max(tsvRowLengthBox.Minimum, store.GetBucketTsvRowLength(bucket)));
        }
    }

    private void RefreshDestinationBuckets(bool resetSelection = false)
    {
        var destProject = DestinationProject;
        var previous = DestinationBucketName;
        destinationBucketBox.Items.Clear();
        destinationBucketBox.Items.AddRange(store.GetBucketDisplayItems(destProject)
            .Select(item => item.Label.Trim())
            .Cast<object>()
            .ToArray());
        destinationBucketBox.Text = !resetSelection && !string.IsNullOrWhiteSpace(previous)
            ? previous
            : destProject.Buckets.FirstOrDefault(bucket => bucket.Id == destProject.ActiveBucketId)?.Name
                ?? destProject.Buckets.FirstOrDefault()?.Name
                ?? "Inbox";
    }

    private void Complete(bool pasteNow, bool saveToBucket, bool unformatted = false)
    {
        RefreshPreview();
        if (!HasSelectedNotes)
        {
            MessageBox.Show(this, "Select at least one bucket with notes to compile.", "Zetl", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (saveToBucket && string.IsNullOrWhiteSpace(DestinationBucketName))
        {
            MessageBox.Show(this, "Name a destination bucket first.", "Zetl", MessageBoxButtons.OK, MessageBoxIcon.Information);
            destinationBucketBox.Focus();
            return;
        }

        PasteNow = pasteNow;
        SaveToBucket = saveToBucket;
        CompiledText = unformatted
            ? store.CompileUnformattedFromNotes(SelectedNotes)
            : previewBox.Text;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void CompleteLastItem()
    {
        if (!store.TryGetLastNoteDisplayItem(sourceProject, sourceScope, out var note, sessionOnlyCheck.Checked) || note is null)
        {
            MessageBox.Show(this, "No current-session note to paste.", "Zetl", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        PasteNow = true;
        SaveToBucket = false;
        CompiledText = note.Note.Text.Trim();
        DialogResult = DialogResult.OK;
        Close();
    }
}
