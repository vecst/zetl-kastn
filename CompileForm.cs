namespace ZETL;

internal sealed partial class CompileForm : ZetlPopupForm
{
    private readonly ZetlStateStore store;
    private readonly ZetlProject project;
    private readonly IReadOnlyList<ZetlBucket>? bucketScope;

    public CompileForm(ZetlStateStore store, ZetlProject project, IReadOnlyList<ZetlBucket>? bucketScope = null)
    {
        this.store = store;
        this.project = project;
        this.bucketScope = bucketScope;

        InitializeComponent();

        var scopedBucketIds = bucketScope?.Select(bucket => bucket.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in store.GetNoteDisplayItems(project, bucketScope))
        {
            noteList.Items.Add(item, scopedBucketIds is not null || item.Bucket.Id == project.ActiveBucketId);
        }

        RefreshDestinationBuckets();
        ApplyBucketCompileDefaults();
        noteList.ItemCheck += (_, _) => BeginInvoke(RefreshPreview);
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

    public string DestinationBucketName => destinationBucketBox.Text.Trim();

    private IReadOnlyList<NoteDisplayItem> SelectedNotes => noteList.CheckedItems.Cast<NoteDisplayItem>().ToList();

    private bool HasSelectedNotes => SelectedNotes.Count > 0;

    private string SelectedCompileMode => compileModeBox.SelectedItem as string ?? "Formatted";

    private int TsvRowLength => Math.Max(1, (int)tsvRowLengthBox.Value);

    private ZetlBucket? DefaultCompileBucket => bucketScope is { Count: 1 }
        ? bucketScope[0]
        : project.Buckets.FirstOrDefault(bucket => bucket.Id == project.ActiveBucketId)
            ?? project.Buckets.FirstOrDefault();

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
            "TSV" => store.CompileTsvFromNotes(project, selected, TsvRowLength),
            _ => store.CompilePlainTextFromNotes(project, selected)
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

    private void RefreshDestinationBuckets()
    {
        var previous = DestinationBucketName;
        destinationBucketBox.Items.Clear();
        destinationBucketBox.Items.AddRange(store.GetBucketDisplayItems(project)
            .Select(item => item.Label.Trim())
            .Cast<object>()
            .ToArray());
        destinationBucketBox.Text = string.IsNullOrWhiteSpace(previous)
            ? project.Buckets.FirstOrDefault(bucket => bucket.Id == project.ActiveBucketId)?.Name
                ?? project.Buckets.FirstOrDefault()?.Name
                ?? "Inbox"
            : previous;
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
        if (!store.TryGetLastNoteDisplayItem(project, bucketScope, out var note) || note is null)
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
