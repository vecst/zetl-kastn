using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ZETL;

// Avalonia port of CompileForm. Completion properties intentionally mirror the
// WinForms form so the runtime host can consume either implementation.
internal partial class CompileWindow : Window
{
    private readonly ZetlStateStore store = null!;
    private readonly List<BucketSelection> selections = [];
    private ZetlProject sourceProject = null!;
    private IReadOnlyList<ZetlBucket>? sourceScope;
    private bool refreshing;
    private bool completionDecided;

    public CompileWindow()
    {
        InitializeComponent();
    }

    internal CompileWindow(
        ZetlStateStore store,
        ZetlProject project,
        IReadOnlyList<ZetlBucket>? bucketScope = null)
    {
        this.store = store;
        sourceProject = project;
        sourceScope = bucketScope;

        InitializeComponent();
        compileModeBox.ItemsSource = new[] { "Formatted", "Plain", "TSV" };

        PopulateProjectSelectors();
        RebuildSelections();
        RefreshDestinationBuckets();
        ApplyBucketCompileDefaults();

        sourceProjectBox.SelectionChanged += (_, _) => OnSourceProjectChanged();
        destinationProjectBox.SelectionChanged += (_, _) =>
            RefreshDestinationBuckets(resetSelection: true);
        sessionOnlyCheck.IsCheckedChanged += (_, _) => RebuildSelections();
        selectAllButton.Click += (_, _) => SetAllChecked(true);
        selectNoneButton.Click += (_, _) => SetAllChecked(false);
        compileModeBox.SelectionChanged += (_, _) =>
        {
            UpdateCompileModeControls();
            RefreshPreview();
        };
        tsvRowLengthBox.ValueChanged += (_, _) => RefreshPreview();
        copyButton.Click += (_, _) => Complete(pasteNow: false, saveToBucket: false);
        pasteButton.Click += (_, _) => Complete(pasteNow: true, saveToBucket: false);
        pastePlainButton.Click += (_, _) =>
            Complete(pasteNow: true, saveToBucket: false, unformatted: true);
        pasteLastButton.Click += (_, _) => CompleteLastItem();
        saveBucketButton.Click += (_, _) => Complete(pasteNow: false, saveToBucket: true);
        cancelButton.Click += (_, _) => Cancel();
        ZetlWindowShortcuts.Enable(
            this,
            () => Complete(pasteNow: false, saveToBucket: false),
            Cancel);
        Deactivated += (_, _) =>
        {
            if (CloseOnDeactivate && !completionDecided)
            {
                ClosedByDeactivate = true;
                completionDecided = true;
                Close();
            }
        };
        Closing += (_, _) => completionDecided = true;

        UpdateCompileModeControls();
        RefreshPreview();
    }

    public bool Saved { get; private set; }

    public bool CloseOnDeactivate { get; set; }

    public bool ClosedByDeactivate { get; private set; }

    public string CompiledText { get; private set; } = "";

    public bool PasteNow { get; private set; }

    public bool SaveToBucket { get; private set; }

    public ZetlProject SourceProject => sourceProject;

    public ZetlProject DestinationProject =>
        destinationProjectBox.SelectedItem as ZetlProject ?? sourceProject;

    public string DestinationBucketName =>
        destinationBucketBox.Text?.Trim() ?? "";

    public bool Flatten => flattenCheck.IsChecked == true;

    public IReadOnlyList<string> SelectedNoteTexts => SelectedNotes
        .Select(item => item.Note.Text.Trim())
        .Where(text => text.Length > 0)
        .ToList();

    private IReadOnlyList<NoteDisplayItem> SelectedNotes => selections
        .SelectMany(group => group.Notes)
        .Where(item => item.CheckBox.IsChecked == true)
        .Select(item => item.Item)
        .ToList();

    private string SelectedCompileMode =>
        compileModeBox.SelectedItem as string ?? "Formatted";

    private int TsvRowLength =>
        Math.Max(1, decimal.ToInt32(tsvRowLengthBox.Value ?? 1));

    private ZetlBucket? DefaultCompileBucket => sourceScope is { Count: 1 }
        ? sourceScope[0]
        : sourceProject.Buckets.FirstOrDefault(
            bucket => bucket.Id == sourceProject.ActiveBucketId)
            ?? sourceProject.Buckets.FirstOrDefault();

    private void PopulateProjectSelectors()
    {
        var projects = store.State.Projects
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        sourceProjectBox.ItemsSource = projects;
        destinationProjectBox.ItemsSource = projects;
        sourceProjectBox.SelectedItem = projects.First(item => item.Id == sourceProject.Id);
        destinationProjectBox.SelectedItem = projects.First(item => item.Id == sourceProject.Id);
    }

    private void OnSourceProjectChanged()
    {
        if (refreshing
            || sourceProjectBox.SelectedItem is not ZetlProject selected
            || selected.Id == sourceProject.Id)
        {
            return;
        }

        sourceProject = selected;
        sourceScope = null;
        RebuildSelections();
        ApplyBucketCompileDefaults();
    }

    private void RebuildSelections()
    {
        if (store is null || sourceProject is null)
        {
            return;
        }

        refreshing = true;
        selections.Clear();
        selectionHost.Children.Clear();

        var scoped = sourceScope is not null;
        var groups = store.GetNoteDisplayItems(
                sourceProject,
                sourceScope,
                sessionOnlyCheck.IsChecked == true)
            .GroupBy(item => item.Bucket);

        foreach (var group in groups)
        {
            var bucketCheckBox = new CheckBox
            {
                Content = $"{group.Key.Name.Trim()} ({group.Count()})",
                FontWeight = FontWeight.SemiBold,
                IsThreeState = true
            };
            var bucketSelection = new BucketSelection(group.Key, bucketCheckBox);
            var notePanel = new StackPanel
            {
                Spacing = 4,
                Margin = new Avalonia.Thickness(22, 2, 0, 0)
            };
            var defaultChecked = scoped || group.Key.Id == sourceProject.ActiveBucketId;

            foreach (var item in group)
            {
                var noteCheckBox = new CheckBox
                {
                    Content = NotePreview(item.Note.Text),
                    IsChecked = defaultChecked,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                var noteSelection = new NoteSelection(item, noteCheckBox);
                bucketSelection.Notes.Add(noteSelection);
                noteCheckBox.IsCheckedChanged += (_, _) =>
                    OnNoteChecked(bucketSelection);
                notePanel.Children.Add(noteCheckBox);
            }

            bucketCheckBox.IsChecked = defaultChecked;
            bucketCheckBox.IsCheckedChanged += (_, _) =>
                OnBucketChecked(bucketSelection);

            var groupPanel = new StackPanel();
            groupPanel.Children.Add(bucketCheckBox);
            groupPanel.Children.Add(notePanel);
            selectionHost.Children.Add(groupPanel);
            selections.Add(bucketSelection);
        }

        emptySelectionText.IsVisible = selections.Count == 0;
        refreshing = false;
        RefreshPreview();
    }

    private void OnBucketChecked(BucketSelection group)
    {
        if (refreshing)
        {
            return;
        }

        refreshing = true;
        var isChecked = group.CheckBox.IsChecked == true;
        foreach (var note in group.Notes)
        {
            note.CheckBox.IsChecked = isChecked;
        }

        refreshing = false;
        RefreshPreview();
    }

    private void OnNoteChecked(BucketSelection group)
    {
        if (refreshing)
        {
            return;
        }

        refreshing = true;
        var checkedCount = group.Notes.Count(note => note.CheckBox.IsChecked == true);
        group.CheckBox.IsChecked = checkedCount switch
        {
            0 => false,
            var count when count == group.Notes.Count => true,
            _ => null
        };
        refreshing = false;
        RefreshPreview();
    }

    private void SetAllChecked(bool value)
    {
        refreshing = true;
        foreach (var group in selections)
        {
            group.CheckBox.IsChecked = value;
            foreach (var note in group.Notes)
            {
                note.CheckBox.IsChecked = value;
            }
        }

        refreshing = false;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (store is null || sourceProject is null)
        {
            return;
        }

        validationText.IsVisible = false;
        var selected = SelectedNotes;
        previewBox.Text = selected.Count == 0
            ? ""
            : BuildCompiledText(selected);
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
        var tsvSelected = string.Equals(
            SelectedCompileMode,
            "TSV",
            StringComparison.OrdinalIgnoreCase);
        tsvRowLengthLabel.IsEnabled = tsvSelected;
        tsvRowLengthBox.IsEnabled = tsvSelected;
    }

    private void ApplyBucketCompileDefaults()
    {
        var bucket = DefaultCompileBucket;
        compileModeBox.SelectedItem = bucket?.DefaultCompileMode is "Plain" or "TSV"
            ? bucket.DefaultCompileMode
            : "Formatted";
        if (bucket is not null)
        {
            tsvRowLengthBox.Value = Math.Clamp(
                store.GetBucketTsvRowLength(bucket),
                1,
                1000);
        }
    }

    private void RefreshDestinationBuckets(bool resetSelection = false)
    {
        if (store is null || sourceProject is null)
        {
            return;
        }

        var destination = DestinationProject;
        var previous = DestinationBucketName;
        var names = store.GetBucketDisplayItems(destination)
            .Select(item => item.Label.Trim())
            .ToList();
        destinationBucketBox.ItemsSource = names;
        destinationBucketBox.Text = !resetSelection && !string.IsNullOrWhiteSpace(previous)
            ? previous
            : destination.Buckets.FirstOrDefault(
                bucket => bucket.Id == destination.ActiveBucketId)?.Name
                ?? destination.Buckets.FirstOrDefault()?.Name
                ?? "Inbox";
    }

    private void Complete(bool pasteNow, bool saveToBucket, bool unformatted = false)
    {
        var selected = SelectedNotes;
        if (selected.Count == 0)
        {
            ShowValidation("Select at least one note to compile.");
            return;
        }

        if (saveToBucket && string.IsNullOrWhiteSpace(DestinationBucketName))
        {
            ShowValidation("Name a destination bucket first.");
            destinationBucketBox.Focus();
            return;
        }

        PasteNow = pasteNow;
        SaveToBucket = saveToBucket;
        CompiledText = unformatted
            ? store.CompileUnformattedFromNotes(selected)
            : BuildCompiledText(selected);
        completionDecided = true;
        Saved = true;
        Close();
    }

    private void CompleteLastItem()
    {
        if (!store.TryGetLastNoteDisplayItem(
                sourceProject,
                sourceScope,
                out var note,
                sessionOnlyCheck.IsChecked == true)
            || note is null)
        {
            ShowValidation("No note matches the current source and session filter.");
            return;
        }

        PasteNow = true;
        SaveToBucket = false;
        CompiledText = note.Note.Text.Trim();
        completionDecided = true;
        Saved = true;
        Close();
    }

    private void Cancel()
    {
        completionDecided = true;
        Close();
    }

    private void ShowValidation(string message)
    {
        validationText.Text = message;
        validationText.IsVisible = true;
    }

    private static string NotePreview(string text)
    {
        var preview = text.ReplaceLineEndings(" ").Trim();
        return preview.Length <= 80
            ? preview
            : $"{preview[..77]}...";
    }

    private sealed record NoteSelection(
        NoteDisplayItem Item,
        CheckBox CheckBox);

    private sealed class BucketSelection(
        ZetlBucket bucket,
        CheckBox checkBox)
    {
        public ZetlBucket Bucket { get; } = bucket;

        public CheckBox CheckBox { get; } = checkBox;

        public List<NoteSelection> Notes { get; } = [];
    }
}
