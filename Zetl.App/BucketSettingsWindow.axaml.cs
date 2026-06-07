using Avalonia.Controls;

namespace ZETL;

internal partial class BucketSettingsWindow : Window
{
    public BucketSettingsWindow()
    {
        InitializeComponent();
    }

    internal BucketSettingsWindow(ZetlBucket bucket, ZetlStateStore store)
    {
        InitializeComponent();

        bucketNameBox.Text = bucket.Name;
        bucketNameBox.IsEnabled = !ZetlStateStore.IsScratchBucket(bucket);
        defaultKindBox.ItemsSource = new[] { "Standard", "Replay" };
        defaultKindBox.SelectedItem = ZetlStateStore.IsReplayKind(bucket.DefaultKind)
            ? "Replay"
            : "Standard";
        compileModeBox.ItemsSource = new[] { "Formatted", "Plain", "TSV" };
        compileModeBox.SelectedItem = bucket.DefaultCompileMode is "Plain" or "TSV"
            ? bucket.DefaultCompileMode
            : "Formatted";
        defaultStartingTextBox.Text = bucket.DefaultStartingText ?? "";
        tsvRowLengthBox.Value = Math.Clamp(store.GetBucketTsvRowLength(bucket), 1, 1000);

        defaultStartingTextBox.TextChanged += (_, _) =>
        {
            var inferred = InferredTsvRowLength;
            if (inferred > 0)
            {
                tsvRowLengthBox.Value = Math.Clamp(inferred, 1, 1000);
            }
        };
        saveButton.Click += (_, _) => Commit();
        cancelButton.Click += (_, _) => Close();
        ZetlWindowShortcuts.Enable(this, Commit, Close);
    }

    public bool Saved { get; private set; }

    public string BucketName => bucketNameBox.Text?.Trim() ?? "";

    public string DefaultKind => defaultKindBox.SelectedItem as string ?? "Standard";

    public string DefaultCompileMode => compileModeBox.SelectedItem as string ?? "Formatted";

    public string DefaultStartingText => defaultStartingTextBox.Text?.Trim() ?? "";

    public int DefaultTsvRowLength => (int)(tsvRowLengthBox.Value ?? 5);

    private int InferredTsvRowLength => SplitLines(defaultStartingTextBox.Text).Count;

    private void Commit()
    {
        if (string.IsNullOrWhiteSpace(BucketName))
        {
            validationText.IsVisible = true;
            bucketNameBox.Focus();
            return;
        }

        Saved = true;
        Close();
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
