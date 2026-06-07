using Avalonia.Controls;

namespace ZETL;

// Avalonia port of ProjectSetupForm. The result properties intentionally match
// the WinForms dialog so shared orchestration can consume either implementation.
internal partial class ProjectSetupWindow : Window
{
    public ProjectSetupWindow()
        : this(null)
    {
    }

    internal ProjectSetupWindow(IReadOnlyList<string>? defaultBuckets)
    {
        InitializeComponent();

        projectNameBox.Text = DateTime.Now.ToString("yyyy-MM-dd");
        bucketNamesBox.Text = string.Join(
            Environment.NewLine,
            defaultBuckets is { Count: > 0 } ? defaultBuckets : new[] { "Inbox", "Scratch" });
        bucketNamesBox.TextChanged += (_, _) => RefreshActiveBuckets();
        createButton.Click += (_, _) => Commit();
        cancelButton.Click += (_, _) => Close();
        ZetlWindowShortcuts.Enable(this, Commit, Close);

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

    private static IReadOnlyList<string> SplitLines(string? text)
    {
        return (text ?? "")
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }
}
