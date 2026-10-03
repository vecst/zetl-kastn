using Avalonia.Controls;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    private readonly KastnEditHistory editHistory;

    private KastnEditHistory CreateEditHistory() => new(
        () => currentProject,
        command => connection.ExecuteAsync(command),
        async () =>
        {
            await connection.RefreshAsync();
            return connection.HasLiveConnection ? connection.Current : null;
        },
        () => connection.DeferRefresh());

    private IDisposable BeginGesture(string description) => editHistory.BeginGesture(description);
    private Task<ZetlResponseEnvelope> ExecuteMutationAsync(ZetlCommandEnvelope command) =>
        editHistory.ExecuteMutationAsync(command);
    private Task UndoLastAsync() => StepHistoryAsync(redo: false);
    private Task RedoLastAsync() => StepHistoryAsync(redo: true);

    private async Task StepHistoryAsync(bool redo)
    {
        if (editHistory.IsStepping)
        {
            statusText.Text = "Wait for the current undo or redo to finish.";
            return;
        }
        if (!IsOnline || currentProject is null)
        {
            statusText.Text = "Connect to Zetl to edit.";
            return;
        }

        var context = new KastnEditorWorkflowContext(currentProject.Id, editorState);
        if (!await SaveEditorAsync() || !IsOnline
            || !context.IsSameSession(currentProject?.Id, editorState)) return;
        if (editHistory.IsStepping)
        {
            statusText.Text = "Wait for the current undo or redo to finish.";
            return;
        }

        // UI selection stays at the boundary; the owner receives snapshots and
        // an async conflict decision, and returns a typed completion outcome.
        var entry = editHistory.Peek(redo);
        var targetSlipId = entry?.Operations.FirstOrDefault()?.SlipId;
        if (targetSlipId is not null) ReselectSlipNode(targetSlipId);
        else if (entry?.BucketOperations.FirstOrDefault() is { } bucket)
            pendingBucketSelectionId = bucket.BucketId;
        context = new KastnEditorWorkflowContext(context.ProjectId, editorState);
        var generation = editHistory.Generation;
        var result = await editHistory.StepAsync(redo, conflict =>
            KastnDialogs.UndoConflictAsync(this, conflict.Verb, conflict.Description,
                conflict.CurrentText, conflict.TargetText));
        if (!context.IsSameSession(currentProject?.Id, editorState)
            || editHistory.Generation != generation) return;
        if (result.Status == KastnHistoryStepStatus.Completed && targetSlipId is not null
            && !editorState.IsDirty)
            ReselectSlipNode(targetSlipId);
        statusText.Text = editorState.IsDirty
            ? editorState.ConflictCurrent is not null ? "Resolve the slip conflict before continuing." : "Unsaved changes."
            : HistoryMessage(result, redo ? "redo" : "undo");
    }

    private static string HistoryMessage(KastnHistoryStepResult result, string verb) => result.Status switch
    {
        KastnHistoryStepStatus.Empty => $"Nothing to {verb}.",
        KastnHistoryStepStatus.Busy => "Wait for the current undo or redo to finish.",
        KastnHistoryStepStatus.Invalidated => "Edit history changed during the operation; undo and redo were cleared after reconciliation.",
        KastnHistoryStepStatus.ConcurrentChange => $"{Capitalize(verb)} completed, but history changed concurrently; refresh before continuing.",
        KastnHistoryStepStatus.OutcomeUnknown => result.HasRepair
            ? $"{Capitalize(verb)} outcome reconciled — repair the partial change before retrying."
            : $"{Capitalize(verb)} outcome unknown — history retained for reconciliation.",
        KastnHistoryStepStatus.Interrupted => result.HasRepair
            ? $"{Capitalize(verb)} interrupted — repair the partial change before retrying."
            : $"{Capitalize(verb)} interrupted — history entry retained.",
        _ => result.Entry!.IsRepair ? $"Repair complete — retry {verb} when ready."
            : result.Kept > 0 ? $"{Capitalize(verb)}: {result.Entry.Description} — {result.Kept} item{Plural(result.Kept)} kept the newer change."
            : result.Applied == 0 ? $"Nothing to {verb} — {result.Entry.Description} already changed."
            : $"{Capitalize(verb)}: {result.Entry.Description}"
    };

    // Incidental text fields retain native undo; the slip editor's tunnel handler
    // claims Ctrl+Z/Ctrl+Y for Kastn's single ordered history.
    private bool IsTextInputFocused() => FocusManager?.GetFocusedElement() is TextBox;
    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
