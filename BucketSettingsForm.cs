namespace ZETL;

internal sealed partial class BucketSettingsForm : ZetlPopupForm
{
    public BucketSettingsForm(ZetlBucket bucket, ZetlStateStore store)
    {
        InitializeComponent();

        bucketNameBox.Text = bucket.Name;
        bucketNameBox.Enabled = !ZetlStateStore.IsScratchBucket(bucket);
        defaultKindBox.SelectedItem = ZetlStateStore.IsReplayKind(bucket.DefaultKind) ? "Replay" : "Standard";
        compileModeBox.SelectedItem = string.IsNullOrWhiteSpace(bucket.DefaultCompileMode)
            ? "Formatted"
            : bucket.DefaultCompileMode;
        defaultStartingTextBox.Text = bucket.DefaultStartingText ?? "";
        tsvRowLengthBox.Value = Math.Min(tsvRowLengthBox.Maximum, Math.Max(tsvRowLengthBox.Minimum, store.GetBucketTsvRowLength(bucket)));

        defaultStartingTextBox.TextChanged += (_, _) =>
        {
            var inferred = InferredTsvRowLength;
            if (inferred > 0)
            {
                tsvRowLengthBox.Value = Math.Min(tsvRowLengthBox.Maximum, Math.Max(tsvRowLengthBox.Minimum, inferred));
            }
        };
        saveButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(BucketName))
            {
                MessageBox.Show(this, "Name the bucket first.", "Zetl", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None;
            }
        };
    }

    public string BucketName => bucketNameBox.Text.Trim();

    public string DefaultKind => defaultKindBox.SelectedItem as string ?? "Standard";

    public string DefaultCompileMode => compileModeBox.SelectedItem as string ?? "Formatted";

    public string DefaultStartingText => defaultStartingTextBox.Text.Trim();

    public int DefaultTsvRowLength => Math.Max(1, (int)tsvRowLengthBox.Value);

    private int InferredTsvRowLength => defaultStartingTextBox.Lines
        .Select(line => line.Trim())
        .Count(line => line.Length > 0);
}
