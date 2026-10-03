using System.Security.Cryptography;
using System.Text.Json;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

// One command's editor session and draft. Autosaves, formatting, pictures, and
// board edits share response and early-snapshot acceptance rules.
internal sealed class KastnEditorMutationAcceptance
{
    private readonly KastnEditorState editor;
    private readonly long selectionVersion;
    private readonly bool targetedEditor;
    private readonly string draftText;
    private readonly IReadOnlyList<ZetlInlineStyleRange> draftStyles;
    private readonly IReadOnlySet<string> pendingStyleKinds;
    private readonly string baselineText;
    private readonly IReadOnlyList<ZetlInlineStyleRange> baselineStyles;
    private readonly UpdateSlipCommand? update;
    private readonly string? pictureHash;
    private readonly string? destinationBucketId;

    public KastnEditorMutationAcceptance(KastnEditorState editor, ZetlCommandEnvelope command, object payload)
    {
        this.editor = editor;
        Command = command;
        selectionVersion = editor.SelectionVersion;
        targetedEditor = command.TargetId == editor.SlipId;
        draftText = editor.DraftText;
        draftStyles = editor.DraftInlineStyles.Select(style => style with { }).ToArray();
        pendingStyleKinds = new HashSet<string>(editor.PendingInlineStyleKinds, StringComparer.Ordinal);
        baselineText = editor.BaselineText;
        baselineStyles = editor.BaselineInlineStyles.Select(style => style with { }).ToArray();
        destinationBucketId = (payload as MoveSlipCommand)?.DestinationBucketId;
        if (command.Kind == ZetlCommandKind.UpdateSlip)
        {
            update = payload as UpdateSlipCommand;
        }
        else if (command.Kind == ZetlCommandKind.SetSlipPicture
            && payload is SetSlipPictureCommand picture)
        {
            pictureHash = Convert.ToHexString(SHA256.HashData(picture.Bytes));
        }
    }

    public ZetlCommandEnvelope Command { get; }
    public bool IsCurrentEditor => targetedEditor && editor.SelectionVersion == selectionVersion
        && editor.SlipId == Command.TargetId;

    public static KastnEditorMutationAcceptance Create<TPayload>(
        KastnEditorState editor, string projectId, ZetlSlipSnapshot slip,
        ZetlCommandKind kind, Func<string, TPayload> buildWithText)
    {
        var isEditing = slip.Id == editor.SlipId;
        var revision = isEditing ? editor.Revision : slip.Revision;
        var text = isEditing ? editor.DraftText.Trim() : slip.Text;
        object payload = buildWithText(text)!;
        if (isEditing && payload is UpdateSlipCommand { InlineStyles: null } update
            && editor.InlineStylesAreDirty)
        {
            payload = update with
            {
                InlineStyles = KastnInlineStyleEditing.ForTrimmedCommand(editor.DraftText, editor.DraftInlineStyles)
            };
        }
        var command = ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"), kind, payload, projectId, slip.Id, revision);
        return new(editor, command, payload);
    }

    public bool TryAcknowledgeSnapshot(ZetlSlipSnapshot? current)
    {
        if (!IsCurrentEditor || current is null || current.Id != Command.TargetId
            || current.Revision != Command.ExpectedTargetRevision + 1 || !MatchesMutation(current))
        {
            return false;
        }

        Accept(current);
        return true;
    }

    public bool TryAcceptResponse(ZetlResponseEnvelope response)
    {
        if (response.Status != ZetlResponseStatus.Success || response.CommandId != Command.CommandId
            || response.ProjectId is { } projectId && projectId != Command.ProjectId
            || response.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options) is not { } saved
            || saved.Id != Command.TargetId || saved.Revision <= Command.ExpectedTargetRevision)
        {
            return false;
        }

        if (IsCurrentEditor)
        {
            Accept(saved);
        }
        return true;
    }

    public bool ReconcileConflict(ZetlResponseEnvelope response) =>
        IsCurrentEditor && response.CommandId == Command.CommandId
        && (response.ProjectId is null || response.ProjectId == Command.ProjectId)
        && editor.ReconcileConflict(response);

    private void Accept(ZetlSlipSnapshot saved)
    {
        // Never roll back an acknowledged save or a newer authoritative revision.
        if (saved.Revision <= editor.Revision)
        {
            return;
        }

        var newerConflict = editor.ConflictCurrent;
        var draftChanged = !string.Equals(editor.DraftText, draftText, StringComparison.Ordinal)
            || !KastnInlineStyleEditing.StyleListsEqual(editor.DraftInlineStyles, draftStyles)
            || !editor.PendingInlineStyleKinds.SetEquals(pendingStyleKinds);
        if (Command.Kind != ZetlCommandKind.UpdateSlip || draftChanged)
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

    private bool MatchesMutation(ZetlSlipSnapshot current)
    {
        if (update is { } payload)
        {
            // Check authored properties too: another command can change formatting
            // without changing the text at the expected next revision.
            return current.Text == payload.Text.Trim()
                && (payload.Title is null || current.Title == payload.Title.Trim())
                && (payload.Type is null || current.Type == (payload.Type == ZetlSlipType.Picture
                    ? ZetlSlipType.Picture : ZetlSlipClassifier.LooksLikeUrl(current.Text)
                        ? ZetlSlipType.Url : ZetlSlipType.Text))
                && (payload.ExcludedFromViews is null || current.ExcludedFromViews == payload.ExcludedFromViews)
                && (payload.Align is null || ZetlViewRenderer.NormalizeHeadingAlign(current.Align)
                    == ZetlViewRenderer.NormalizeHeadingAlign(payload.Align))
                && (payload.BlockKind is null || current.BlockKind == ZetlBlockKinds.Normalize(payload.BlockKind))
                && (payload.IgnoreBucketRenderKind is null || current.IgnoreBucketRenderKind == payload.IgnoreBucketRenderKind)
                && (payload.Checked is null || current.Checked == (payload.BlockKind is not null
                    && ZetlBlockKinds.Normalize(payload.BlockKind) != ZetlBlockKinds.Task ? false : payload.Checked))
                && (payload.Bold is null || current.Bold == payload.Bold)
                && (payload.Italic is null || current.Italic == payload.Italic)
                && (payload.Strike is null || current.Strike == payload.Strike)
                && (payload.FontFamily is null || current.FontFamily == ZetlSlipTypography.NormalizeFontFamily(payload.FontFamily))
                && (payload.FontSize is null || current.FontSize == ZetlSlipTypography.NormalizeFontSize(payload.FontSize.Value))
                && (payload.TextColor is null || current.TextColor == ZetlSlipTypography.NormalizeTextColor(payload.TextColor))
                && (payload.InlineStyles is null || KastnInlineStyleEditing.StyleListsEqual(
                    current.InlineStyles, ZetlInlineStyles.Normalize(current.Text, payload.InlineStyles)));
        }

        return current.Text == baselineText
            && KastnInlineStyleEditing.StyleListsEqual(current.InlineStyles, baselineStyles)
            && (Command.Kind == ZetlCommandKind.RemoveSlipPicture && current.Picture is null
                || Command.Kind == ZetlCommandKind.MoveSlip
                    && destinationBucketId is not null && current.BucketId == destinationBucketId
                // Deleted is created lazily, so its bucket ID may not yet be
                // present in the captured project. Check soft-delete metadata.
                || Command.Kind == ZetlCommandKind.DeleteSlip && current.DeletedAtUtc is not null
                    && current.DeletedFromBucketId is not null
                || Command.Kind == ZetlCommandKind.SetSlipPicture && pictureHash is not null
                    && string.Equals(current.Picture?.Sha256, pictureHash, StringComparison.OrdinalIgnoreCase));
    }
}
