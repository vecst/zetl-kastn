namespace ZETL;

/// <summary>
/// The Board editor's hold on one slip: the slip it shows and the text it was
/// loaded with. The store updates slips in place, so the slip's own Text is
/// always the current stored text, which another writer (Kastn's save on focus
/// loss, a capture) can change while the Board is open. Comparing the editor
/// with that live text would mistake the other writer's change for typing in
/// the Board, keep the stale text on screen, and save it back over the newer
/// one. Only a difference from the loaded text counts as an edit.
/// </summary>
internal sealed class ZetlBoardNoteDraft
{
    public ZetlSlip? Slip { get; private set; }

    public string LoadedText { get; private set; } = "";

    public void Load(ZetlSlip? slip)
    {
        Slip = slip;
        LoadedText = slip?.Text ?? "";
    }

    /// <summary>
    /// True when the editor holds typing the Board has not written. Raw,
    /// untrimmed comparison, so a trailing space being typed still counts.
    /// </summary>
    public bool IsEdited(string? editorText) =>
        Slip is not null && !string.Equals(editorText ?? "", LoadedText, StringComparison.Ordinal);

    /// <summary>
    /// The text to write for the slip, or null when there is nothing to write:
    /// the editor is untouched, or already matches the store. A text slip is never
    /// emptied from the Board; a picture's caption may be cleared.
    /// </summary>
    public string? TextToSave(string? editorText)
    {
        if (Slip is not { } slip || !IsEdited(editorText))
        {
            return null;
        }

        var text = editorText?.Trim() ?? "";
        if (text.Length == 0 && !slip.IsImage)
        {
            return null;
        }

        return string.Equals(slip.Text, text, StringComparison.Ordinal) ? null : text;
    }
}
