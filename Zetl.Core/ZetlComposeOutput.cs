namespace ZETL;

// What Compose produces from the slips the user picked: formatted text and
// HTML (with or without the project and bucket headings), unformatted text,
// and TSV rows. Pure: slips in, text out.
internal static class ZetlComposeOutput
{
    public static string PlainText(ZetlProject project, IEnumerable<SlipDisplayItem> selectedNotes)
    {
        var parts = new List<string> { project.Name.Trim(), "" };
        foreach (var group in selectedNotes.GroupBy(item => item.Bucket))
        {
            var depth = ZetlTreeText.BucketDepth(group.Key, project.Buckets);
            parts.Add(ZetlTreeText.IndentedLine(group.Key.Name.Trim(), depth));
            parts.AddRange(group
                .Where(item => !item.Slip.IsImage)
                .Select(item => ZetlTreeText.IndentedText(item.Slip.Text, depth + 1)));
            parts.Add("");
        }

        return string.Join(Environment.NewLine, parts).TrimEnd();
    }

    // headings=false leaves out the project title and bucket headings, for
    // pasting slips into something that already has its own.
    public static string Html(
        ZetlProject project,
        IEnumerable<SlipDisplayItem> selectedNotes,
        bool headings = true)
    {
        var selected = selectedNotes
            .Where(item => !item.Slip.IsImage)
            .Select(item => ZetlProjectSnapshotMapper.ToSnapshot(item.Bucket, item.Slip))
            .ToList();
        var html = ZetlViewRenderer.RenderHtmlBody(
            ZetlProjectSnapshotMapper.ToSnapshot(project),
            selected,
            new ZetlViewDocument
            {
                Id = "compile-formatted-html",
                Name = "Formatted",
                Kind = ZetlViewKinds.Html,
                ShowTitle = headings
            },
            headings: headings);
        return PrintableTaskBoxes(html);
    }

    // Clipboard targets such as word processors drop form inputs, so a quick
    // worksheet prints its task boxes as characters instead.
    private static string PrintableTaskBoxes(string html) =>
        html
            .Replace(ZetlMarkdown.TaskCheckboxHtml(isChecked: true), "☑ ", StringComparison.Ordinal)
            .Replace(ZetlMarkdown.TaskCheckboxHtml(isChecked: false), "☐ ", StringComparison.Ordinal);

    public static string Unformatted(IEnumerable<SlipDisplayItem> selectedNotes)
    {
        return string.Join(
            Environment.NewLine,
            selectedNotes
                .Select(item => item.Slip.Text.Trim())
                .Where(text => text.Length > 0));
    }

    public static string Tsv(IEnumerable<SlipDisplayItem> selectedNotes, int rowLength) =>
        string.Join(Environment.NewLine, TsvLines(selectedNotes, rowLength));

    // The rows of a TSV compose, for the text and the HTML table alike.
    public static List<string> TsvLines(IEnumerable<SlipDisplayItem> selectedNotes, int rowLength) =>
        ZetlTsv.Table(selectedNotes
            .GroupBy(item => item.Bucket)
            .Select(group => (
                ZetlTsv.HeaderCells(group.Key.Settings.DefaultStartingText),
                group.Select(item => item.Slip.Text),
                rowLength)));

    public static int TsvRowLength(ZetlBucket bucket)
    {
        var headerLength = ZetlTsv.HeaderCells(bucket.Settings.DefaultStartingText).Count;
        return headerLength > 0 ? headerLength : Math.Max(1, bucket.Settings.DefaultTsvRowLength);
    }
}
