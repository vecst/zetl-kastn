namespace ZETL;

internal sealed partial class ProjectSetupForm : ZetlPopupForm
{
    public ProjectSetupForm(IReadOnlyList<string>? defaultBuckets = null)
    {
        InitializeComponent();
        projectNameBox.Text = DateTime.Now.ToString("yyyy-MM-dd");
        if (defaultBuckets is { Count: > 0 })
        {
            bucketNamesBox.Text = string.Join(Environment.NewLine, defaultBuckets);
        }

        bucketNamesBox.TextChanged += (_, _) => RefreshActiveBuckets();
        saveButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(ProjectName))
            {
                MessageBox.Show(this, "Name the project first.", "Zetl", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None;
            }
        };
        RefreshActiveBuckets();
    }

    public string ProjectName => projectNameBox.Text.Trim();

    public IReadOnlyList<string> BucketNames => bucketNamesBox.Lines
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .ToList();

    public string? ActiveBucketName => activeBucketBox.SelectedItem as string;

    private void RefreshActiveBuckets()
    {
        var previous = ActiveBucketName;
        var names = BucketNames.ToList();
        if (!names.Any(name => string.Equals(name, "Scratch", StringComparison.OrdinalIgnoreCase)))
        {
            names.Add("Scratch");
        }

        if (names.Count == 0)
        {
            names.Add("Inbox");
        }

        activeBucketBox.Items.Clear();
        activeBucketBox.Items.AddRange(names.Cast<object>().ToArray());
        activeBucketBox.SelectedItem = names.FirstOrDefault(name => string.Equals(name, previous, StringComparison.OrdinalIgnoreCase))
            ?? names.First();
    }
}
