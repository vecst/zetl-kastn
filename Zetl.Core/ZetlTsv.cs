namespace ZETL;

// Tab-separated output shared by Zetl's Compose and the TSV view.
internal static class ZetlTsv
{
    // One table from several buckets' slips, ready to paste into a
    // spreadsheet: no project title or bucket names, just rows. A bucket's
    // header row comes first, and is repeated only when the next bucket's
    // headers differ.
    public static List<string> Table(
        IEnumerable<(IReadOnlyList<string> Headers, IEnumerable<string> Values, int RowLength)> groups)
    {
        var lines = new List<string>();
        string? lastHeader = null;
        foreach (var (headers, values, rowLength) in groups)
        {
            if (headers.Count > 0 && string.Join('\t', headers) is var header && header != lastHeader)
            {
                lines.Add(header);
                lastHeader = header;
            }

            lines.AddRange(Rows(values, Math.Max(1, rowLength)));
        }

        return lines;
    }

    // The same table as HTML. Spreadsheets paste an HTML table straight into
    // cells, where plain text sends LibreOffice Calc to its import dialog with
    // whatever separators it last used.
    public static string HtmlTable(IEnumerable<string> lines) =>
        "<table>"
        + string.Concat(lines.Select(line =>
            "<tr>" + string.Concat(line.Split('\t').Select(cell => $"<td>{ZetlHtml.Escape(cell)}</td>")) + "</tr>"))
        + "</table>";

    // One cell: line breaks and tabs become spaces so the value stays in its cell.
    public static string Cell(string text) =>
        text.ReplaceLineEndings(" ").Replace('\t', ' ').Trim();

    // A bucket's header row, taken from its starting text: one column per line.
    public static IReadOnlyList<string> HeaderCells(string? startingText) =>
        (startingText ?? "")
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Select(Cell)
            .Where(text => text.Length > 0)
            .ToList();

    // Non-empty values laid out rowLength cells to a row.
    public static IEnumerable<string> Rows(IEnumerable<string> values, int rowLength)
    {
        var cells = values.Select(Cell).Where(text => text.Length > 0).ToList();
        for (var i = 0; i < cells.Count; i += rowLength)
        {
            yield return string.Join('\t', cells.Skip(i).Take(rowLength));
        }
    }
}
