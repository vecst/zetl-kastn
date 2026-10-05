using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    // Native prompts belong to the interaction that opened them.
    // Sent commands still finish; only their later UI effects are conditional.
    private Func<bool> CaptureNativeAction(bool trackEditor = true)
    {
        var session = CurrentNavigationSession();
        var intent = navigation.IntentVersion;
        var section = landing.Section;
        var archived = landing.ShowingArchived;
        var consumable = landing.ShowingConsumable;
        var editor = new KastnEditorWorkflowContext(session.ProjectId ?? "", editorState);
        var templateSession = templateEditor.State;
        var creationSession = creationEditor.State;
        var viewSession = viewEditor.State;
        var templateDraft = templateEditor.Capture();
        var creationDraft = creationEditor.Capture();
        var viewDraft = viewEditor.Capture();
        var templateKey = templateDraft is null ? null : KastnCatalogEditorSession<ZetlTemplateDocument>.Fingerprint(templateDraft);
        var creationKey = creationDraft is null ? null : KastnCatalogEditorSession<ZetlCreationTypeDocument>.Fingerprint(creationDraft);
        var viewKey = viewDraft is null ? null : KastnViewEditorDraft.Fingerprint(viewDraft);
        return () => !lifetime.AllowClose && CurrentNavigationSession() == session && !session.Retired
            && navigation.IntentVersion == intent
            && landing.Section == section && landing.ShowingArchived == archived && landing.ShowingConsumable == consumable
            && (!trackEditor || editor.IsSameDraft(currentProject?.Id ?? "", editorState))
            && ReferenceEquals(templateEditor.State, templateSession) && ReferenceEquals(creationEditor.State, creationSession)
            && ReferenceEquals(viewEditor.State, viewSession)
            && (templateKey is null || templateEditor.Capture() is { } t && KastnCatalogEditorSession<ZetlTemplateDocument>.Fingerprint(t) == templateKey)
            && (creationKey is null || creationEditor.Capture() is { } c && KastnCatalogEditorSession<ZetlCreationTypeDocument>.Fingerprint(c) == creationKey)
            && (viewKey is null || viewEditor.Capture() is { } v && KastnViewEditorDraft.Fingerprint(v) == viewKey);
    }

    private bool CurrentProjectTarget(KastnProjectCard card)
    {
        if (!IsOnline) return false;
        var latest = connection.Current.Projects.FirstOrDefault(project => project.Id == card.Id);
        return latest is not null ? latest.MetadataRevision == card.MetadataRevision && latest.Status == card.Status
            : NativeProject is { } project && project.Id == card.Id
                && project.MetadataRevision == card.MetadataRevision && project.Status == card.Status;
    }

    private ZetlProjectSnapshot? NativeProject => connection.Current.Project is { } latest
        && latest.Id == currentProject?.Id && latest.ChangeSequence > currentProject.ChangeSequence ? latest : currentProject;

    private bool CurrentBucketTarget(ZetlBucketSnapshot bucket) => NativeProject?.Buckets
        .Any(current => current.Id == bucket.Id && current.Revision == bucket.Revision) == true;

    private async Task<ZetlResponseEnvelope?> RunNativeCommandAsync(ZetlCommandEnvelope command, string success, Func<bool> canSend,
        Action<ZetlResponseEnvelope>? beforeRefresh = null)
    {
        if (!IsOnline || !canSend()) return null;
        var current = CaptureNativeAction(trackEditor: false);
        using var busy = mutations.TryBeginWrite();
        if (busy is null) return null;
        try
        {
            var response = await ExecuteMutationAsync(command);
            // A changed editor/selection never receives a completion's selection
            // or clearing effects. The authoritative snapshot still reconciles.
            if (response.Status == ZetlResponseStatus.Success)
            {
                if (current() && canSend()) beforeRefresh?.Invoke(response);
                await connection.SynchronizeAsync();
            }
            if (current() && !editorState.IsDirty) HandleSimpleResponse(response, success);
            return response;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (current() && !editorState.IsDirty) statusText.Text = ex.Message;
            return null;
        }
    }

    private async Task DeleteNativeCatalogAsync<T>(T source, Func<Task<bool>> confirm,
        Func<T?> loadCurrent, Action delete, Action refresh, string kind, string name) where T : class
    {
        if (lifetime.IsRetired || lifetime.AllowClose) return;
        var fingerprint = KastnCatalogEditorSession<T>.Fingerprint(source);
        bool SameSource() => loadCurrent() is { } latest && KastnCatalogEditorSession<T>.Fingerprint(latest) == fingerprint;
        var current = CaptureNativeAction();
        try
        {
            if (!SameSource() || !await confirm() || !current()) return;
            if (!SameSource()) return;
            delete();
            refresh();
            RefreshLandingMode();
            statusText.Text = $"Deleted {kind} '{name}'.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (current()) statusText.Text = $"Could not delete {kind}: {ex.Message}";
        }
    }
}
