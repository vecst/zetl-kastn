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
        ZetlWindowPlacement.Track(this);

        bucketNameBox.Text = bucket.Name;
        bucketNameBox.IsEnabled = !ZetlStateStore.IsScratchBucket(bucket);
        defaultKindBox.ItemsSource = new[] { "Standard", "Replay" };
        defaultKindBox.SelectedItem = ZetlStateStore.IsReplayKind(bucket.Settings.DefaultKind)
            ? "Replay"
            : "Standard";
        compileModeBox.ItemsSource = new[] { "Formatted", "Plain", "TSV" };
        compileModeBox.SelectedItem = bucket.Settings.DefaultCompileMode is "Plain" or "TSV"
            ? bucket.Settings.DefaultCompileMode
            : "Formatted";
        defaultStartingTextBox.Text = bucket.Settings.DefaultStartingText ?? "";
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

    private int InferredTsvRowLength => ZetlDialogText.SplitLines(defaultStartingTextBox.Text).Count;

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

}
