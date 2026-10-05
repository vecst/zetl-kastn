using Avalonia.Threading;
using System.Text.Json;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    private readonly KastnMutationCoordinator mutations;
    private bool saving => mutations.IsBusy;
    private Task<bool>? inflightSave => mutations.InflightSave;
    private bool addingSlip => mutations.IsPreparing(KastnMutationPreparation.AddSlip);
    private bool visibilityUpdating => mutations.IsPreparing(KastnMutationPreparation.Visibility);
    private bool boardEditing => mutations.IsPreparing(KastnMutationPreparation.BoardEdit);
    private bool applyingDrop => mutations.IsPreparing(KastnMutationPreparation.Drop);
    private bool savingVisual;
    private DispatcherTimer? savingVisualTimer;

    private void OnMutationStateChanged()
    {
        if (lifetime.IsRetired) return;
        if (saving)
        {
            if (!savingVisual)
            {
                if (savingVisualTimer is null)
                {
                    savingVisualTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    savingVisualTimer.Tick += (_, _) =>
                    {
                        savingVisualTimer.Stop();
                        if (lifetime.IsRetired || !saving) return;
                        savingVisual = true;
                        SetEditingEnabled();
                    };
                }
                if (!savingVisualTimer.IsEnabled) savingVisualTimer.Start();
            }
        }
        else
        {
            savingVisualTimer?.Stop();
            savingVisual = false;
        }
        SetEditingEnabled();
    }

    private KastnMutationContext CaptureMutationContext() => new(CurrentNavigationSession(), navigation.IntentVersion, editorState);
    private bool IsCurrentMutation(KastnMutationContext context) => IsOnline
        && context.Matches(CurrentNavigationSession(), navigation.IntentVersion, editorState);
    private bool IsCurrentMutationScope(KastnMutationContext context) => IsOnline
        && context.MatchesScope(CurrentNavigationSession(), navigation.IntentVersion);
    private void ShowMutationCompletion(KastnMutationContext context, string message)
    {
        if (!IsCurrentMutationScope(context)) return;
        mutations.SetNotice(connection.Current, CurrentNavigationSession(), navigation.IntentVersion, message);
        statusText.Text = message;
    }

    private string? MutationStatus(KastnSessionSnapshot snapshot) =>
        mutations.NoticeFor(snapshot, connection.Current, CurrentNavigationSession(), navigation.IntentVersion);

    private async Task<ZetlResponseEnvelope> ExecuteSlipBatchCommandAsync(ZetlCommandEnvelope command, KastnMutationContext context)
    {
        object payload = command.Kind switch
        {
            ZetlCommandKind.UpdateSlip => command.Payload!.Value.Deserialize<UpdateSlipCommand>(ZetlProtocolJson.Options)!,
            ZetlCommandKind.MoveSlip => command.Payload!.Value.Deserialize<MoveSlipCommand>(ZetlProtocolJson.Options)!,
            ZetlCommandKind.DeleteSlip => new DeleteSlipCommand(),
            _ => throw new InvalidOperationException("Unsupported slip batch command.")
        };
        var acceptance = new KastnEditorMutationAcceptance(editorState, command, payload);
        pendingEditorMutation = acceptance;
        try
        {
            var response = await ExecuteMutationAsync(command);
            if (IsCurrentMutation(context))
            {
                if (acceptance.TryAcceptResponse(response) && acceptance.IsCurrentEditor)
                {
                    PersistEditorAfterSave(context.ProjectId);
                    UpdateEditorFromState();
                }
                else if (acceptance.ReconcileConflict(response)) ShowConflict();
            }
            return response;
        }
        finally { if (ReferenceEquals(pendingEditorMutation, acceptance)) pendingEditorMutation = null; }
    }
}
