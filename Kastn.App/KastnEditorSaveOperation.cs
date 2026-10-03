using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal sealed record KastnEditorSaveResult(
    bool CanLeaveEditor,
    bool Saved = false,
    string? Message = null);

// Captures the draft sent to Zetl. Snapshots can acknowledge it before the
// command returns, while the editor may already contain more typing.
internal sealed class KastnEditorSaveOperation
{
    private readonly KastnEditorState editor;
    private readonly KastnEditorMutationAcceptance acceptance;

    private KastnEditorSaveOperation(
        KastnEditorState editor,
        ZetlProjectSnapshot project,
        UpdateSlipCommand payload)
    {
        this.editor = editor;
        Command = ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.UpdateSlip,
            payload,
            project.Id,
            editor.SlipId,
            editor.Revision);
        acceptance = new(editor, Command, payload);
    }

    public ZetlCommandEnvelope Command { get; }
    public string ProjectId => Command.ProjectId!;
    public bool IsCurrentEditor => acceptance.IsCurrentEditor;

    public static KastnEditorSaveOperation? Create(
        KastnEditorState editor,
        ZetlProjectSnapshot project)
    {
        var slip = project.Slips.FirstOrDefault(slip => slip.Id == editor.SlipId);
        if (slip is null || !editor.IsDirty || editor.ConflictCurrent is not null)
        {
            return null;
        }

        var refreshedText = ZetlSlipLinks.RefreshCachedTitles(editor.DraftText, project);
        var text = refreshedText.Trim();
        if (text.Length == 0 && string.IsNullOrWhiteSpace(slip.Title) && slip.Type != ZetlSlipType.Picture)
        {
            return null;
        }

        return new KastnEditorSaveOperation(editor, project, new UpdateSlipCommand
        {
            Text = text,
            InlineStyles = editor.InlineStylesAreDirty
                ? KastnInlineStyleEditing.ForTrimmedCommand(
                    refreshedText,
                    KastnInlineStyleEditing.ReconcileTextEdit(
                        editor.DraftText, refreshedText, editor.DraftInlineStyles))
                : null
        });
    }

    public bool TryAcknowledgeSnapshot(ZetlSlipSnapshot? current) =>
        acceptance.TryAcknowledgeSnapshot(current);

    public async Task<KastnEditorSaveResult> ExecuteAsync(
        Func<ZetlCommandEnvelope, Task<ZetlResponseEnvelope>> execute)
    {
        try
        {
            var response = await execute(Command);
            if (response.Status == ZetlResponseStatus.Conflict)
            {
                acceptance.ReconcileConflict(response);
                return new(false);
            }

            if (response.Status != ZetlResponseStatus.Success)
            {
                return new(false, Message: response.Error?.Message ?? $"Save failed: {response.Status}.");
            }

            if (!acceptance.TryAcceptResponse(response))
            {
                return new(false, Message: "The save response did not confirm the updated slip.");
            }

            if (!IsCurrentEditor)
            {
                return new(false, Saved: true);
            }

            var canLeave = !editor.IsDirty && editor.ConflictCurrent is null;
            return new(canLeave, Saved: true, Message: editor.ConflictCurrent is not null
                ? "Resolve the slip conflict before continuing."
                : canLeave ? "Slip saved." : "Slip saved; newer edits are still unsaved.");
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            return new(false, Message: $"Slip was not saved. {ex.Message}");
        }
    }
}
