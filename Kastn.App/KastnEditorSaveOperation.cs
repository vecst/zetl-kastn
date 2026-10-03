using System.Text.Json;
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
    private readonly long selectionVersion;
    private readonly string draftText;
    private readonly IReadOnlyList<ZetlInlineStyleRange> draftStyles;
    private readonly IReadOnlySet<string> pendingStyleKinds;
    private readonly UpdateSlipCommand payload;

    private KastnEditorSaveOperation(
        KastnEditorState editor,
        ZetlProjectSnapshot project,
        UpdateSlipCommand payload)
    {
        this.editor = editor;
        this.payload = payload;
        selectionVersion = editor.SelectionVersion;
        draftText = editor.DraftText;
        draftStyles = editor.DraftInlineStyles.Select(style => style with { }).ToList();
        pendingStyleKinds = new HashSet<string>(editor.PendingInlineStyleKinds, StringComparer.Ordinal);
        Command = ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.UpdateSlip,
            payload,
            project.Id,
            editor.SlipId,
            editor.Revision);
    }

    public ZetlCommandEnvelope Command { get; }
    public string ProjectId => Command.ProjectId!;
    public bool IsCurrentEditor => editor.SelectionVersion == selectionVersion;

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

    public bool TryAcknowledgeSnapshot(ZetlSlipSnapshot? current)
    {
        if (!IsCurrentEditor
            || current is null
            || current.Id != Command.TargetId
            || current.Revision != Command.ExpectedTargetRevision + 1
            || !string.Equals(current.Text, payload.Text, StringComparison.Ordinal)
            || payload.InlineStyles is { } styles
                && !KastnInlineStyleEditing.StyleListsEqual(current.InlineStyles, styles))
        {
            return false;
        }

        Accept(current);
        return true;
    }

    public async Task<KastnEditorSaveResult> ExecuteAsync(
        Func<ZetlCommandEnvelope, Task<ZetlResponseEnvelope>> execute)
    {
        try
        {
            var response = await execute(Command);
            if (response.Status == ZetlResponseStatus.Conflict)
            {
                if (IsCurrentEditor)
                {
                    editor.ReconcileConflict(response);
                }
                return new(false);
            }

            if (response.Status != ZetlResponseStatus.Success)
            {
                return new(false, Message: response.Error?.Message ?? $"Save failed: {response.Status}.");
            }

            var saved = response.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options);
            if (saved is null || saved.Id != Command.TargetId || saved.Revision <= Command.ExpectedTargetRevision)
            {
                return new(false, Message: "The save response did not confirm the updated slip.");
            }

            if (!IsCurrentEditor)
            {
                return new(false, Saved: true);
            }

            Accept(saved);
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

    private void Accept(ZetlSlipSnapshot saved)
    {
        // A snapshot may have already acknowledged this command, or a newer
        // authoritative revision may have arrived while its response was delayed.
        if (saved.Revision <= editor.Revision)
        {
            return;
        }

        var newerConflict = editor.ConflictCurrent;
        var draftChanged = !string.Equals(editor.DraftText, draftText, StringComparison.Ordinal)
            || !KastnInlineStyleEditing.StyleListsEqual(editor.DraftInlineStyles, draftStyles)
            || !editor.PendingInlineStyleKinds.SetEquals(pendingStyleKinds);
        if (draftChanged)
        {
            editor.AcceptSavedKeepDraft(saved);
        }
        else
        {
            editor.AcceptSaved(saved);
        }

        if (newerConflict is { } conflict && conflict.Revision > saved.Revision)
        {
            editor.Reconcile(conflict);
        }
    }
}
