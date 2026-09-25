namespace ZETL;

// Tab-separated output shared by Zetl's quick compile and the TSV view.
internal static class ZetlTsv
{
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
