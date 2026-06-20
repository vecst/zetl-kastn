using Avalonia.Controls;

namespace ZETL;

// Avalonia port of ProjectSetupForm. The result properties intentionally match
// the WinForms dialog so shared orchestration can consume either implementation.
internal partial class ProjectSetupWindow : Window
{
    // The bucket text restored when "Custom buckets" is reselected after a
    // template was previewed.
    private readonly string defaultBucketText;
    // The dated default name; used to decide whether choosing a template may
    // helpfully retitle the project without clobbering a name the user typed.
    private readonly string initialName;

    public ProjectSetupWindow()
        : this(null)
    {
    }

    internal ProjectSetupWindow(
        IReadOnlyList<string>? defaultBuckets,
        IReadOnlyList<ZetlTemplateDocument>? templates = null)
    {
        InitializeComponent();
        ZetlWindowPlacement.Track(this);

        initialName = DateTime.Now.ToString("yyyy-MM-dd");
        projectNameBox.Text = initialName;
        defaultBucketText = string.Join(
            Environment.NewLine,
            ZetlBucketDefaults.ResolveProjectBuckets(defaultBuckets));
        bucketNamesBox.Text = defaultBucketText;
        bucketNamesBox.TextChanged += (_, _) => RefreshActiveBuckets();
        createButton.Click += (_, _) => Commit();
        cancelButton.Click += (_, _) => Close();
        ZetlWindowShortcuts.Enable(this, Commit, Close);

        InitializeTemplates(templates);

        RefreshActiveBuckets();
        Opened += (_, _) =>
        {
            projectNameBox.Focus();
            projectNameBox.SelectAll();
        };
    }

    public bool Saved { get; private set; }

    public string ProjectName => projectNameBox.Text?.Trim() ?? "";

    public IReadOnlyList<string> BucketNames => SplitLines(bucketNamesBox.Text);

    public string? ActiveBucketName => activeBucketBox.SelectedItem as string;

    // The chosen template, or null for the quick "Custom buckets" path. When set,
    // the caller creates the project from the template document (buckets, settings,
    // and seeds) rather than from the bucket-name lines.
    public ZetlTemplateDocument? SelectedTemplate =>
        (templateBox.SelectedItem as TemplateChoice)?.Template;

    private void InitializeTemplates(IReadOnlyList<ZetlTemplateDocument>? templates)
    {
        if (templates is not { Count: > 0 })
        {
            return;
        }

        var choices = new List<TemplateChoice> { new("Custom buckets", null) };
        choices.AddRange(templates.Select(template => new TemplateChoice(
            $"{template.Name} ({template.Category})", template)));
        templateBox.ItemsSource = choices;
        templateBox.SelectedIndex = 0;
        templateBox.SelectionChanged += (_, _) => OnTemplateChoiceChanged();
        templatePanel.IsVisible = true;
    }

    private void OnTemplateChoiceChanged()
    {
        var template = SelectedTemplate;
        if (template is null)
        {
            bucketNamesBox.Text = defaultBucketText;
            bucketNamesBox.IsEnabled = true;
            activeBucketBox.IsEnabled = true;
            RefreshActiveBuckets();
            return;
        }

        // Preview the template's buckets; settings and seeds come from the document
        // on create, so the bucket and active-bucket boxes become read-only.
        bucketNamesBox.Text = string.Join(
            Environment.NewLine,
            template.Buckets.Select(bucket => bucket.Name));
        bucketNamesBox.IsEnabled = false;
        activeBucketBox.IsEnabled = false;
        RefreshActiveBuckets();

        // Offer the template's name only while the title is still the untouched
        // dated default, so a name the user typed is never overwritten.
        if (string.Equals(projectNameBox.Text, initialName, StringComparison.Ordinal))
        {
            projectNameBox.Text = template.Name;
        }
    }

    private void Commit()
    {
        if (string.IsNullOrWhiteSpace(ProjectName))
        {
            validationText.Text = "Name the project first.";
            validationText.IsVisible = true;
            projectNameBox.Focus();
            return;
        }

        Saved = true;
        Close();
    }

    private void RefreshActiveBuckets()
    {
        var previous = ActiveBucketName;
        var names = BucketNames.ToList();
        if (!names.Any(name => string.Equals(name, "Scratch", StringComparison.OrdinalIgnoreCase)))
        {
            names.Add("Scratch");
        }

        activeBucketBox.ItemsSource = names;
        activeBucketBox.SelectedItem = names.FirstOrDefault(
            name => string.Equals(name, previous, StringComparison.OrdinalIgnoreCase))
            ?? names.First();
    }

    // A template-picker entry. ToString supplies the ComboBox label; a null
    // Template is the quick "Custom buckets" path.
    private sealed record TemplateChoice(string Label, ZetlTemplateDocument? Template)
    {
        public override string ToString() => Label;
    }

    private static IReadOnlyList<string> SplitLines(string? text)
    {
        return (text ?? "")
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }
}
