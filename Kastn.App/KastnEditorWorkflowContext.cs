using ZETL.Contracts;

namespace KASTN;

// Captures the editor that initiated a workflow before a save or dialog await.
// Dialog offsets require the same draft; a batch can allow later typing while
// still requiring the original project and editor session.
internal sealed class KastnEditorWorkflowContext
{
    private readonly long selectionVersion;
    private readonly string? slipId;
    private readonly string text;
    private readonly IReadOnlyList<ZetlInlineStyleRange> styles;
    private readonly IReadOnlySet<string> pendingStyles;

    public KastnEditorWorkflowContext(string projectId, KastnEditorState editor)
    {
        ProjectId = projectId;
        selectionVersion = editor.SelectionVersion;
        slipId = editor.SlipId;
        text = editor.DraftText;
        styles = editor.DraftInlineStyles.Select(style => style with { }).ToArray();
        pendingStyles = new HashSet<string>(editor.PendingInlineStyleKinds, StringComparer.Ordinal);
    }

    public string ProjectId { get; }

    public bool IsSameSession(string? projectId, KastnEditorState editor) =>
        projectId == ProjectId && editor.SelectionVersion == selectionVersion && editor.SlipId == slipId;

    public bool IsSameDraft(string? projectId, KastnEditorState editor) =>
        editor.ConflictCurrent is null && IsSameDraftContent(projectId, editor);

    public bool IsSameDraftContent(string? projectId, KastnEditorState editor) =>
        IsSameSession(projectId, editor) && editor.DraftText == text
        && KastnInlineStyleEditing.StyleListsEqual(editor.DraftInlineStyles, styles)
        && editor.PendingInlineStyleKinds.SetEquals(pendingStyles);
}
