using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using ZETL;

namespace KASTN;

internal sealed record KastnTemplateEditorControls(TextBlock Title, TextBox Name, TextBox Category,
    TextBox Description, ComboBox Type, CheckBox Temporary, ListBox Buckets, Control BucketEditor,
    Control EmptyHint, TextBox BucketName, ComboBox Kind, ComboBox Compile, NumericUpDown Tsv,
    TextBox StartingText, TextBox Cards, Control SeedsPanel, Button Add, Button Delete, Button Up, Button Down);

internal sealed class KastnTemplateBucketRow(ZetlTemplateBucketDocument bucket) : INotifyPropertyChanged
{
    public ZetlTemplateBucketDocument Bucket { get; } = bucket;
    public string Label => string.IsNullOrWhiteSpace(Bucket.Name) ? "(unnamed bucket)" : Bucket.Name;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Renamed() => PropertyChanged?.Invoke(this, new(nameof(Label)));
}

// Owns template fields, bucket selection/order, and buffered field capture. UI
// values are read at save/cancel/selection boundaries, rather than trusting that
// every TextChanged event has already been dispatched.
internal sealed class KastnTemplateEditorPresenter
{
    private static readonly string[] Kinds = ["Standard", "Replay"];
    private static readonly string[] CompileModes = ["Formatted", "Plain", "TSV"];
    private readonly KastnTemplateEditorControls controls;
    private readonly Action<string> showError;
    private readonly ObservableCollection<KastnTemplateBucketRow> rows = [];
    private readonly Dictionary<ZetlTemplateBucketDocument, ZetlTemplateBucketDocument> originalBuckets = [];
    private ZetlTemplateBucketDocument? selected;
    private bool updating;
    private string displayedKind = "", displayedCompile = "";
    private decimal displayedTsv;
    public KastnCatalogEditorSession<ZetlTemplateDocument>? State { get; private set; }

    public KastnTemplateEditorPresenter(KastnTemplateEditorControls controls, Action<string> showError)
    {
        this.controls = controls;
        this.showError = showError;
        controls.Buckets.ItemsSource = rows;
        controls.Type.ItemsSource = new[] { ZetlTemplateTypes.Capture, ZetlTemplateTypes.Consumable };
        controls.Kind.ItemsSource = Kinds;
        controls.Compile.ItemsSource = CompileModes;
        controls.Buckets.SelectionChanged += (_, _) => SelectBucket();
        controls.Type.SelectionChanged += (_, _) => ApplyType();
        controls.Temporary.IsCheckedChanged += (_, _) => ApplyType();
        controls.BucketName.TextChanged += (_, _) => CommitBucket();
        controls.Kind.SelectionChanged += (_, _) => CommitBucket();
        controls.Compile.SelectionChanged += (_, _) => CommitBucket();
        controls.Tsv.ValueChanged += (_, _) => CommitBucket();
        controls.StartingText.TextChanged += (_, _) => CommitBucket();
        controls.Cards.TextChanged += (_, _) => CommitBucket();
        controls.Add.Click += (_, _) => AddBucket();
        controls.Delete.Click += (_, _) => DeleteBucket();
        controls.Up.Click += (_, _) => MoveBucket(-1);
        controls.Down.Click += (_, _) => MoveBucket(1);
    }

    public void Open(ZetlTemplateDocument source, bool isNew)
    {
        Close();
        State = new(source, doc => doc.Id, (doc, id) => doc.Id = id,
            doc => ZetlTemplateDefaults.CreateId(doc.Name), ZetlTemplateValidator.Validate);
        var document = State.Document;
        if (document.Buckets.Count == 0) document.Buckets.Add(new() { Name = "Inbox" });
        foreach (var bucket in document.Buckets) originalBuckets[bucket] = JsonFile.Clone(bucket);
        updating = true;
        try
        {
            controls.Title.Text = isNew ? "New Template" : $"Edit Template — {document.Name}";
            controls.Name.Text = document.Name;
            controls.Category.Text = document.Category;
            controls.Description.Text = document.Description;
            controls.Type.SelectedItem = document.IsConsumable ? ZetlTemplateTypes.Consumable : ZetlTemplateTypes.Capture;
            controls.Temporary.IsChecked = document.Temporary && document.IsConsumable;
        }
        finally { updating = false; }
        Rebuild(0);
        ApplyType();
        State.SetBaseline(Capture()!);
    }

    public ZetlTemplateDocument? Capture()
    {
        if (State is null) return null;
        CommitBucket();
        var result = JsonFile.Clone(State.Document);
        result.Name = controls.Name.Text?.Trim() ?? "";
        result.Category = string.IsNullOrWhiteSpace(controls.Category.Text) ? "Custom" : controls.Category.Text.Trim();
        result.Description = controls.Description.Text?.Trim() ?? "";
        result.Type = controls.Type.SelectedItem as string ?? ZetlTemplateTypes.Capture;
        result.Temporary = result.IsConsumable && controls.Temporary.IsChecked == true;
        return result;
    }

    private void ApplyType()
    {
        if (updating || State is null) return;
        var consumable = controls.Type.SelectedItem as string == ZetlTemplateTypes.Consumable;
        controls.Temporary.IsEnabled = consumable;
        if (!consumable) controls.Temporary.IsChecked = false;
        controls.SeedsPanel.IsVisible = true;
    }

    private void Rebuild(int index)
    {
        updating = true;
        try
        {
            rows.Clear();
            if (State is not null)
                foreach (var bucket in State.Document.Buckets) rows.Add(new(bucket));
            controls.Buckets.SelectedIndex = rows.Count == 0 ? -1 : Math.Clamp(index, 0, rows.Count - 1);
        }
        finally { updating = false; }
        selected = null;
        SelectBucket();
    }

    private void SelectBucket()
    {
        if (updating) return;
        CommitBucket();
        selected = (controls.Buckets.SelectedItem as KastnTemplateBucketRow)?.Bucket;
        controls.BucketEditor.IsVisible = selected is not null;
        controls.EmptyHint.IsVisible = selected is null;
        if (selected is not { } bucket) return;
        updating = true;
        try
        {
            controls.BucketName.Text = bucket.Name;
            var original = originalBuckets[bucket];
            displayedKind = KindChoice(original.Settings.Kind);
            displayedCompile = CompileChoice(original.Settings.DefaultCompileMode);
            displayedTsv = Math.Clamp(original.Settings.DefaultTsvRowLength, 1, 100);
            controls.Kind.SelectedItem = KindChoice(bucket.Settings.Kind);
            controls.Compile.SelectedItem = CompileChoice(bucket.Settings.DefaultCompileMode);
            controls.Tsv.Value = Math.Clamp(bucket.Settings.DefaultTsvRowLength, 1, 100);
            controls.StartingText.Text = bucket.Settings.DefaultStartingText;
            controls.Cards.Text = FormatCards(bucket);
        }
        finally { updating = false; }
    }

    private void CommitBucket()
    {
        if (updating || State is null || selected is not { } bucket || !State.Document.Buckets.Contains(bucket)) return;
        var name = controls.BucketName.Text?.Trim() ?? "";
        if (name != bucket.Name)
        {
            bucket.Name = name;
            rows.FirstOrDefault(row => row.Bucket == bucket)?.Renamed();
        }
        var original = originalBuckets[bucket];
        var kind = controls.Kind.SelectedItem as string ?? "Standard";
        bucket.Settings.Kind = kind == displayedKind ? original.Settings.Kind : kind;
        bucket.Settings.DefaultKind = kind == displayedKind ? original.Settings.DefaultKind : kind;
        var compile = controls.Compile.SelectedItem as string ?? "Formatted";
        bucket.Settings.DefaultCompileMode = compile == displayedCompile ? original.Settings.DefaultCompileMode : compile;
        var tsv = controls.Tsv.Value ?? 5;
        bucket.Settings.DefaultTsvRowLength = tsv == displayedTsv ? original.Settings.DefaultTsvRowLength : (int)tsv;
        bucket.Settings.DefaultStartingText = controls.StartingText.Text ?? "";
        var text = controls.Cards.Text ?? "";
        if (text == FormatCards(original))
        {
            bucket.Seeds = original.Seeds.ToList();
            bucket.Cards = original.Cards.Select(JsonFile.Clone).ToList();
        }
        else
        {
            bucket.Seeds = [];
            bucket.Cards = ParseCards(text);
        }
    }

    private void AddBucket()
    {
        if (State is null) return;
        CommitBucket();
        var bucket = new ZetlTemplateBucketDocument();
        State.Document.Buckets.Add(bucket);
        originalBuckets[bucket] = JsonFile.Clone(bucket);
        Rebuild(State.Document.Buckets.Count - 1);
        controls.BucketName.Focus();
    }

    private void DeleteBucket()
    {
        if (State is null || selected is not { } bucket) return;
        CommitBucket();
        if (State.Document.Buckets.Count <= 1) { showError("A template needs at least one bucket."); return; }
        var index = State.Document.Buckets.IndexOf(bucket);
        State.Document.Buckets.Remove(bucket);
        originalBuckets.Remove(bucket);
        Rebuild(Math.Max(0, index - 1));
    }

    private void MoveBucket(int delta)
    {
        if (State is null || selected is not { } bucket) return;
        CommitBucket();
        var index = State.Document.Buckets.IndexOf(bucket);
        var destination = index + delta;
        if (index < 0 || destination < 0 || destination >= State.Document.Buckets.Count) return;
        State.Document.Buckets.RemoveAt(index);
        State.Document.Buckets.Insert(destination, bucket);
        Rebuild(destination);
    }

    public void Close()
    {
        State = null;
        selected = null;
        originalBuckets.Clear();
        updating = true;
        try { rows.Clear(); controls.BucketEditor.IsVisible = false; controls.EmptyHint.IsVisible = true; }
        finally { updating = false; }
    }

    private static string KindChoice(string value) => Kinds.FirstOrDefault(kind =>
        string.Equals(kind, value, StringComparison.OrdinalIgnoreCase)) ?? "Standard";
    private static string CompileChoice(string value) => CompileModes.FirstOrDefault(mode =>
        string.Equals(mode, value, StringComparison.OrdinalIgnoreCase)) ?? "Formatted";

    internal static List<ZetlTemplateSlipDocument> ParseCards(string? text) => (text ?? "")
        .Replace("\r\n", "\n").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)
        .Select(line =>
        {
            var separator = line.IndexOf("::", StringComparison.Ordinal);
            return separator < 0 ? new ZetlTemplateSlipDocument { Text = line }
                : new ZetlTemplateSlipDocument { Title = line[..separator].Trim(), Text = line[(separator + 2)..].Trim() };
        }).ToList();

    internal static string FormatCards(ZetlTemplateBucketDocument bucket) => string.Join("\n",
        bucket.Seeds.Concat(bucket.Cards.Select(card => string.IsNullOrWhiteSpace(card.Title)
            ? card.Text : $"{card.Title} :: {card.Text}".TrimEnd())));
}
