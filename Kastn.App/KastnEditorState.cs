using System.Text.Json;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal sealed class KastnEditorState
{
    public long SelectionVersion { get; private set; }
    public string? SlipId { get; private set; }
    public long Revision { get; private set; }
    public string BaselineText { get; private set; } = "";
    public string DraftText { get; private set; } = "";
    public IReadOnlyList<ZetlInlineStyleRange> BaselineInlineStyles { get; private set; } = [];
    public IReadOnlyList<ZetlInlineStyleRange> DraftInlineStyles { get; private set; } = [];
    public IReadOnlySet<string> PendingInlineStyleKinds { get; private set; } =
        new HashSet<string>(StringComparer.Ordinal);
    public ZetlSlipSnapshot? ConflictCurrent { get; private set; }
    public bool IsDirty => SlipId is not null
        && (!string.Equals(DraftText, BaselineText, StringComparison.Ordinal)
            || !KastnInlineStyleEditing.StyleListsEqual(DraftInlineStyles, BaselineInlineStyles));

    public bool InlineStylesAreDirty => SlipId is not null
        && !KastnInlineStyleEditing.StyleListsEqual(DraftInlineStyles, BaselineInlineStyles);

    public void Select(ZetlSlipSnapshot? slip)
    {
        SelectionVersion++;
        SlipId = slip?.Id;
        Revision = slip?.Revision ?? 0;
        BaselineText = slip?.Text ?? "";
        DraftText = BaselineText;
        BaselineInlineStyles = CopyInlineStyles(slip?.InlineStyles);
        DraftInlineStyles = BaselineInlineStyles;
        PendingInlineStyleKinds = new HashSet<string>(StringComparer.Ordinal);
        ConflictCurrent = null;
    }

    // The crash-recovery journal entry for the current draft; RestoreDraft is its
    // inverse.
    public KastnDraftDocument ToDraftDocument(string projectId) => new()
    {
        ProjectId = projectId,
        SlipId = SlipId ?? "",
        BaselineRevision = Revision,
        BaselineText = BaselineText,
        BaselineInlineStyles = CopyInlineStyles(BaselineInlineStyles).ToList(),
        DraftText = DraftText,
        DraftInlineStyles = CopyInlineStyles(DraftInlineStyles).ToList(),
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    public bool RestoreDraft(KastnDraftDocument draft, ZetlSlipSnapshot current)
    {
        if (!string.Equals(draft.SlipId, current.Id, StringComparison.Ordinal))
        {
            return false;
        }

        SelectionVersion++;
        SlipId = current.Id;
        Revision = draft.BaselineRevision;
        BaselineText = draft.BaselineText ?? "";
        DraftText = draft.DraftText ?? "";
        BaselineInlineStyles = CopyInlineStyles(draft.BaselineInlineStyles);
        DraftInlineStyles = CopyInlineStyles(draft.DraftInlineStyles);
        PendingInlineStyleKinds = new HashSet<string>(StringComparer.Ordinal);
        ConflictCurrent = null;

        if (!IsDirty)
        {
            Select(current);
            return false;
        }

        var authoritativeChanged = current.Revision != draft.BaselineRevision
            || !string.Equals(current.Text, BaselineText, StringComparison.Ordinal)
            || !KastnInlineStyleEditing.StyleListsEqual(
                current.InlineStyles,
                BaselineInlineStyles);
        if (authoritativeChanged)
        {
            ConflictCurrent = current;
        }

        return true;
    }

    public void SetDraft(string text)
    {
        DraftText = text;
    }

    public void ApplyTextEdit(string text)
    {
        var reconciled = KastnInlineStyleEditing.ReconcileTextEdit(
            DraftText,
            text,
            DraftInlineStyles);
        if (PendingInlineStyleKinds.Count > 0)
        {
            var (start, length) = KastnInlineStyleEditing.StyleTargetForSelection(text, 0, 0);
            if (length > 0)
            {
                foreach (var kind in PendingInlineStyleKinds)
                {
                    reconciled = KastnInlineStyleEditing.ToggleTextStyle(
                        text,
                        reconciled,
                        start,
                        length,
                        kind);
                }

                PendingInlineStyleKinds = new HashSet<string>(StringComparer.Ordinal);
            }
        }

        DraftInlineStyles = reconciled;
        DraftText = text;
    }

    public void SetInlineStyles(IReadOnlyList<ZetlInlineStyleRange> inlineStyles)
    {
        DraftInlineStyles = CopyInlineStyles(inlineStyles);
        PendingInlineStyleKinds = new HashSet<string>(StringComparer.Ordinal);
    }

    public bool TogglePendingInlineStyle(string kind)
    {
        kind = ZetlInlineStyleKinds.Normalize(kind);
        if (kind.Length == 0)
        {
            return false;
        }

        var pending = new HashSet<string>(PendingInlineStyleKinds, StringComparer.Ordinal);
        var enabled = pending.Add(kind);
        if (!enabled)
        {
            pending.Remove(kind);
        }

        PendingInlineStyleKinds = pending;
        return enabled;
    }

    public bool HasPendingInlineStyle(string kind) =>
        PendingInlineStyleKinds.Contains(ZetlInlineStyleKinds.Normalize(kind));

    public void Reconcile(ZetlSlipSnapshot? current)
    {
        if (SlipId is null || current is null || current.Id != SlipId)
        {
            return;
        }

        if (current.Revision <= Revision
            || ConflictCurrent is { } conflict && current.Revision <= conflict.Revision)
        {
            return;
        }

        if (IsDirty)
        {
            ConflictCurrent = current;
            return;
        }

        Accept(current, keepDraft: false);
    }

    public bool ReconcileConflict(ZetlResponseEnvelope response)
    {
        if (response.Conflict is not { TargetKind: ZetlEntityKind.Slip } conflict
            || !string.Equals(conflict.TargetId, SlipId, StringComparison.Ordinal))
        {
            return false;
        }

        var current = conflict.Current.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options);
        if (current is null)
        {
            return false;
        }

        Reconcile(current);
        return true;
    }

    public void AcceptSaved(ZetlSlipSnapshot slip)
    {
        Accept(slip, keepDraft: false);
    }

    // A saved mutation that did not touch the slip's text (attaching or removing
    // its picture, moving it, or soft-deleting it): advance the baseline and
    // revision but keep the draft, preserving typing without false conflicts.
    public void AcceptSavedKeepDraft(ZetlSlipSnapshot slip)
    {
        Accept(slip, keepDraft: true);
    }

    public void UseCurrent()
    {
        if (ConflictCurrent is { } current)
        {
            Accept(current, keepDraft: false);
        }
    }

    public void PrepareOverwrite()
    {
        if (ConflictCurrent is { } current)
        {
            Revision = current.Revision;
            BaselineText = current.Text;
            BaselineInlineStyles = CopyInlineStyles(current.InlineStyles);
            PendingInlineStyleKinds = new HashSet<string>(StringComparer.Ordinal);
            ConflictCurrent = null;
        }
    }

    private void Accept(ZetlSlipSnapshot slip, bool keepDraft)
    {
        SlipId = slip.Id;
        Revision = slip.Revision;
        BaselineText = slip.Text;
        if (!keepDraft)
        {
            DraftText = slip.Text;
            DraftInlineStyles = CopyInlineStyles(slip.InlineStyles);
            PendingInlineStyleKinds = new HashSet<string>(StringComparer.Ordinal);
        }

        BaselineInlineStyles = CopyInlineStyles(slip.InlineStyles);
        ConflictCurrent = null;
    }

    private static IReadOnlyList<ZetlInlineStyleRange> CopyInlineStyles(
        IReadOnlyList<ZetlInlineStyleRange>? inlineStyles) =>
        inlineStyles?.Select(style => style with { }).ToList() ?? [];
}
