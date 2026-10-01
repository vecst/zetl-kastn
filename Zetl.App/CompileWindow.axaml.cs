using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ZETL;

// The Compose window (held Ctrl+V): gathers slips into one piece of text to
// paste, copy, or save. Completion properties form its host-facing result.
internal partial class CompileWindow : ZetlPopupWindow
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
        IReadOnlyList<ZetlBucket>? bucketScope = null,
        bool ctrlEnterPastes = true,
        bool headings = true)
    {
        this.store = store;
        sourceProject = project;
        sourceScope = bucketScope;

        InitializeComponent();
        headingsCheck.IsChecked = headings;
        headingsCheck.IsCheckedChanged += (_, _) => RefreshPreview();
        // The button Ctrl+Enter presses is the one drawn as primary.
        (ctrlEnterPastes ? pasteButton : copyButton).Classes.Add("primary");
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
        finishButton.Click += (_, _) => FinishProject();
        cancelButton.Click += (_, _) => Cancel();
        ZetlWindowShortcuts.Enable(
            this,
            () => Complete(pasteNow: ctrlEnterPastes, saveToBucket: false),
            Cancel);

        UpdateCompileModeControls();
        UpdateFinishButton();
        RefreshPreview();
    }

    public bool Saved { get; private set; }

    public string CompiledText { get; private set; } = "";

    public string? CompiledHtml { get; private set; }

    public bool PasteNow { get; private set; }

    public bool SaveToBucket { get; private set; }

    public ZetlProject SourceProject => sourceProject;

    public ZetlProject DestinationProject =>
        destinationProjectBox.SelectedItem as ZetlProject ?? sourceProject;

    public string DestinationBucketName =>
        destinationBucketBox.Text?.Trim() ?? "";

    public bool Flatten => flattenCheck.IsChecked == true;

    // Whether Formatted output carries the project and bucket headings; the
    // host remembers it for the next Compose.
    public bool Headings => headingsCheck.IsChecked == true;

    public IReadOnlyList<string> SelectedNoteTexts => SelectedNotes
        .Select(item => item.Slip.Text.Trim())
        .Where(text => text.Length > 0)
        .ToList();

    private IReadOnlyList<SlipDisplayItem> SelectedNotes => selections
        .SelectMany(group => group.Slips)
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
        // Most recently written first, so the projects worth compiling sit at
        // the top of both pickers.
        var projects = store.GetProjectsByRecentWrite();
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
        UpdateFinishButton();
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
        var groups = store.GetSlipDisplayItems(
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
                    Content = ZetlStateStore.PreviewText(item.Slip.Text),
                    IsChecked = defaultChecked,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                var noteSelection = new NoteSelection(item, noteCheckBox);
                bucketSelection.Slips.Add(noteSelection);
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
        foreach (var note in group.Slips)
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
        var checkedCount = group.Slips.Count(note => note.CheckBox.IsChecked == true);
        group.CheckBox.IsChecked = checkedCount switch
        {
            0 => false,
            var count when count == group.Slips.Count => true,
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
            foreach (var note in group.Slips)
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

    private string BuildCompiledText(IReadOnlyList<SlipDisplayItem> selected)
    {
        return SelectedCompileMode switch
        {
            "Plain" => store.CompileUnformattedFromSlips(selected),
            "TSV" => store.CompileTsvFromSlips(sourceProject, selected, TsvRowLength),
            _ when !Headings => store.CompileUnformattedFromSlips(selected),
            _ => store.CompilePlainTextFromSlips(sourceProject, selected)
        };
    }

    // The rich form of the result: Formatted as styled HTML, and TSV as an HTML
    // table so spreadsheets paste it into cells.
    private string? BuildCompiledHtml(IReadOnlyList<SlipDisplayItem> selected) =>
        SelectedCompileMode switch
        {
            "Formatted" => store.CompileHtmlFromSlips(sourceProject, selected, Headings),
            "TSV" => ZetlTsv.HtmlTable(store.CompileTsvLinesFromSlips(selected, TsvRowLength)),
            _ => null
        };

    private void UpdateCompileModeControls()
    {
        var tsvSelected = string.Equals(
            SelectedCompileMode,
            "TSV",
            StringComparison.OrdinalIgnoreCase);
        tsvRowLengthLabel.IsEnabled = tsvSelected;
        tsvRowLengthBox.IsEnabled = tsvSelected;
        // Plain and TSV never carry headings; a TSV is just the table.
        headingsCheck.IsEnabled = string.Equals(SelectedCompileMode, "Formatted", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyBucketCompileDefaults()
    {
        var bucket = DefaultCompileBucket;
        compileModeBox.SelectedItem = bucket?.Settings.DefaultCompileMode is "Plain" or "TSV"
            ? bucket.Settings.DefaultCompileMode
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
            ShowValidation("Select at least one slip to compose.");
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
            ? store.CompileUnformattedFromSlips(selected)
            : BuildCompiledText(selected);
        CompiledHtml = !saveToBucket && !unformatted
            ? BuildCompiledHtml(selected)
            : null;
        completionDecided = true;
        Saved = true;
        Close();
    }

    private void CompleteLastItem()
    {
        if (!store.TryGetLastSlipDisplayItem(
                sourceProject,
                sourceScope,
                out var note,
                sessionOnlyCheck.IsChecked == true)
            || note is null)
        {
            ShowValidation("No slip matches the current source and session filter.");
            return;
        }

        PasteNow = true;
        SaveToBucket = false;
        CompiledText = note.Slip.Text.Trim();
        CompiledHtml = null;
        completionDecided = true;
        Saved = true;
        Close();
    }

    // Seal the source project (Finished) and clear it from its lane, then close.
    // Finishing produces no compile output ("seal + advance only"), so it closes
    // like Cancel (Saved stays false); the host performs no paste or save. The
    // store mutation is the sole-writer FinishProject and persists on its own.
    private void FinishProject()
    {
        if (!ZetlStateStore.IsActiveStatus(sourceProject))
        {
            ShowValidation("This project is already finished.");
            return;
        }

        store.FinishProject(sourceProject.Id);
        completionDecided = true;
        Close();
    }

    private void UpdateFinishButton()
    {
        // Only an Active, non-infrastructure project can be finished.
        finishButton.IsEnabled = ZetlStateStore.IsActiveStatus(sourceProject)
            && !string.Equals(
                sourceProject.Name,
                ZetlStateStore.LogProjectName,
                StringComparison.OrdinalIgnoreCase);
    }

    private void Cancel()
    {
        completionDecided = true;
        Close();
    }

    protected override bool IsDismissSuppressed => completionDecided;

    protected override void OnClickAwayDismiss()
    {
        completionDecided = true;
        Close();
    }

    protected override void OnPopupClosing()
    {
        completionDecided = true;
    }

    private void ShowValidation(string message)
    {
        validationText.Text = message;
        validationText.IsVisible = true;
    }

    private sealed record NoteSelection(
        SlipDisplayItem Item,
        CheckBox CheckBox);

    private sealed class BucketSelection(
        ZetlBucket bucket,
        CheckBox checkBox)
    {
        public ZetlBucket Bucket { get; } = bucket;

        public CheckBox CheckBox { get; } = checkBox;

        public List<NoteSelection> Slips { get; } = [];
    }
}
