using Avalonia.Threading;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    private void ScheduleDraftJournal()
    {
        if (!editorState.IsDirty)
        {
            return;
        }

        if (draftJournalTimer is null)
        {
            draftJournalTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            draftJournalTimer.Tick += (_, _) =>
            {
                draftJournalTimer.Stop();
                FlushDraftJournal();
            };
        }

        draftJournalTimer.Stop();
        draftJournalTimer.Start();
    }

    private bool FlushDraftJournal()
    {
        draftJournalTimer?.Stop();
        if (!editorState.IsDirty
            || currentProject is null
            || editorState.SlipId is null)
        {
            return false;
        }

        var draft = editorState.ToDraftDocument(currentProject.Id);
        if (!draftStore.Save(draft))
        {
            return false;
        }

        // This journal came from the live editor. A following snapshot must not
        // restore it over newer typing as though it came from a previous session.
        restoredDraftKey = DraftRecoveryKey(draft);
        return true;
    }

    private bool ClearDraftJournal(string? projectId, string? slipId)
    {
        draftJournalTimer?.Stop();
        var draft = draftStore.Draft;
        if (draft is not null
            && string.Equals(draft.ProjectId, projectId, StringComparison.Ordinal)
            && string.Equals(draft.SlipId, slipId, StringComparison.Ordinal))
        {
            return draftStore.Clear();
        }

        return true;
    }

    private void PersistEditorAfterSave(string projectId)
    {
        if (editorState.IsDirty)
        {
            FlushDraftJournal();
        }
        else
        {
            recoveredDraftActive = false;
            ClearDraftJournal(projectId, editorState.SlipId);
        }
    }

    private string? RecoverySlipId(ZetlProjectSnapshot project)
    {
        var draft = draftStore.Draft;
        return draft is not null
            && string.Equals(draft.ProjectId, project.Id, StringComparison.Ordinal)
            && project.Slips.Any(slip => string.Equals(slip.Id, draft.SlipId, StringComparison.Ordinal))
                ? draft.SlipId
                : null;
    }

    private void RestoreDraftIfAvailable(ZetlProjectSnapshot project)
    {
        var draft = draftStore.Draft;
        if (draft is null
            || !string.Equals(draft.ProjectId, project.Id, StringComparison.Ordinal))
        {
            return;
        }

        var key = DraftRecoveryKey(draft);
        if (string.Equals(restoredDraftKey, key, StringComparison.Ordinal))
        {
            return;
        }

        var current = project.Slips.FirstOrDefault(slip =>
            string.Equals(slip.Id, draft.SlipId, StringComparison.Ordinal));
        if (current is null)
        {
            statusText.Text = "An unsaved recovery draft exists, but its slip is unavailable.";
            return;
        }

        restoredDraftKey = key;
        if (!editorState.RestoreDraft(draft, current))
        {
            draftStore.Clear();
            return;
        }

        recoveredDraftActive = true;
        UpdateEditorFromState();
        if (editorState.ConflictCurrent is not null)
        {
            ShowConflict();
            statusText.Text = "Recovered an unsaved draft; Zetl changed the slip, so choose which version to keep.";
        }
        else
        {
            statusText.Text = "Recovered an unsaved local draft. It has not yet been saved to Zetl.";
        }
    }

    private static string DraftRecoveryKey(KastnDraftDocument draft) =>
        $"{draft.ProjectId}|{draft.SlipId}|{draft.UpdatedAtUtc.UtcTicks}";

    private async Task<bool> PrepareEditorForExitAsync()
    {
        if (!editorState.IsDirty && editorState.ConflictCurrent is null)
        {
            return true;
        }

        // Make the local copy durable before attempting IPC. A crash or forced
        // updater stop during the save round-trip must still leave recovery data.
        var recoveryStored = FlushDraftJournal();
        if (await SaveEditorAsync())
        {
            return true;
        }

        recoveryStored = FlushDraftJournal() || recoveryStored;
        var action = await KastnDialogs.DecideUnsavedCloseAsync(
            this,
            recoveryStored,
            editorState.ConflictCurrent is not null
                ? "This slip has an unresolved conflict and could not be saved."
                : IsOnline
                    ? "This slip could not be saved to Zetl."
                    : "Kastn is offline, so this slip could not be saved to Zetl.");
        switch (action)
        {
            case KastnDialogs.UnsavedCloseAction.KeepRecovery when recoveryStored:
                return true;
            case KastnDialogs.UnsavedCloseAction.Discard:
                if (ClearDraftJournal(currentProject?.Id, editorState.SlipId))
                {
                    return true;
                }
                statusText.Text = "The local recovery draft could not be removed; closing was cancelled.";
                return false;
            default:
                return false;
        }
    }

    private async Task CompleteWindowCloseAsync()
    {
        if (closeRequestInProgress)
        {
            return;
        }

        closeRequestInProgress = true;
        try
        {
            if (!await PrepareEditorForExitAsync())
            {
                return;
            }

            allowWindowClose = true;
            Close();
        }
        finally
        {
            if (!allowWindowClose)
            {
                closeRequestInProgress = false;
            }
        }
    }
}
