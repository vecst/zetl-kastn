using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    private KastnViewEditorContext ViewEditorContext() => new(currentProject, editHistory.Generation);

    private KastnViewEditorPresenter CreateViewEditorPresenter() => new(
        new KastnViewEditorControls(
            viewAllBucketsButton,
            viewAllBucketsPanel,
            viewCustomSectionsButton,
            viewCustomSectionsPanel,
            viewDescriptionBox,
            viewDocumentSettingsPanel,
            viewEditorTitle,
            viewFormattedKindButton,
            viewHtmlKindButton,
            viewKindBox,
            viewListStyleBox,
            viewLivePreviewPanel,
            viewLivePreviewSummary,
            viewMarkdownKindButton,
            viewNameBox,
            viewNumberHeadingsCheck,
            viewPdfKindButton,
            viewPlainKindButton,
            viewSectionEditorPanel,
            viewShowTitleCheck,
            viewTitleBox,
            viewTsvKindButton,
            viewTsvPanel,
            viewTsvRowBox,
            addViewSectionButton), ViewEditorContext, CreateSlipContentRenderer,
        () => CurrentAppSettings().KastnPreferSlipKindOverBucketKind);

    private void EditSelectedView()
    {
        var view = SelectedView;
        var builtIn = ZetlViewDefaults.IsBuiltIn(view.Id);
        OpenViewEditor(builtIn ? ZetlViewDefaults.Duplicate(view) : view, builtIn);
    }

    private void OpenViewEditor(ZetlViewDocument working, bool isNew)
    {
        viewEditor.Open(working, isNew, !isNew && viewCatalog.IsProjectScoped(working.Id));
        viewErrorText.IsVisible = false;
        emptyState.IsVisible = false;
        projectView.IsVisible = false;
        templateEditorView.IsVisible = false;
        viewEditorView.IsVisible = true;
    }

    private void CloseViewEditor()
    {
        viewEditor.Close();
        viewErrorText.IsVisible = false;
        viewEditorView.IsVisible = false;
        projectView.IsVisible = currentProject is not null;
        emptyState.IsVisible = currentProject is null;
    }

    private Task CancelViewEditAsync() => CancelViewEditAsync(() =>
        KastnDialogs.ConfirmAsync(this, "Discard unsaved changes to this view?", "Discard"));

    private async Task CancelViewEditAsync(Func<Task<bool>> confirm)
    {
        if (viewEditor.State is not { } session || viewEditor.Capture() is not { } draft) return;
        var fingerprint = KastnViewEditorDraft.Fingerprint(draft);
        if (session.IsDirty && !await confirm()) return;
        // A dialog for an older draft cannot discard newer writing or a new session.
        if (ReferenceEquals(viewEditor.State, session)
            && KastnViewEditorDraft.Fingerprint(viewEditor.Capture()!) == fingerprint)
            CloseViewEditor();
    }

    private async Task SaveViewAsync()
    {
        if (viewEditor.State is not { } session || viewEditor.Capture() is not { } draft) return;
        if (viewWriteInProgress)
        {
            ShowViewError("Wait for the current view write to finish.");
            return;
        }
        var context = ViewEditorContext();
        if ((session.ProjectScoped || draft.Sections.Count > 0) && !session.Context.Matches(context))
        {
            ShowViewError("The project session changed. Reopen this view in its original project before saving.");
            return;
        }
        if (!session.HasCurrentProjectView(context.Project))
        {
            ShowViewError("This view changed or was removed in Zetl. Reopen it or save a new copy before editing.");
            return;
        }
        if (string.IsNullOrEmpty(draft.Id))
        {
            // Keep the assigned ID in this session, including after failed writes
            // or later typing, so retrying cannot create another document.
            draft.Id = ZetlViewDefaults.CreateId(draft.Name);
            session.Document.Id = draft.Id;
        }
        viewWriteInProgress = true;
        saveViewSettingsButton.IsEnabled = false;
        try
        {
            var result = await viewPersistence.SaveAsync(draft, session.ProjectScoped, context.Project,
                ExecuteMutationAsync, () => ReferenceEquals(viewEditor.State, session) && context.Matches(ViewEditorContext()));
            if (!ReferenceEquals(viewEditor.State, session) || !context.Matches(ViewEditorContext())) return;
            if (!result.Success)
            {
                ShowViewError(result.Error!);
                return;
            }
            session.AcceptSaved(draft);
            await connection.SynchronizeAsync();
            if (!ReferenceEquals(viewEditor.State, session) || !context.Matches(ViewEditorContext())
                || connection.Current.Project?.Id != context.Project?.Id) return;
            ApplySnapshot(connection.Current);
            if (!context.Matches(ViewEditorContext())) return;
            var unchanged = KastnViewEditorDraft.Fingerprint(viewEditor.Capture()!) == KastnViewEditorDraft.Fingerprint(draft);
            RefreshViewCatalog(currentProject, unchanged ? draft.Id : (viewPickerBox.SelectedItem as ZetlViewDocument)?.Id);
            RefreshViewer();
            if (unchanged) CloseViewEditor();
            else viewErrorText.IsVisible = false;
            statusText.Text = unchanged ? $"Saved view '{draft.Name}'." : $"Saved view '{draft.Name}'; newer edits remain unsaved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            if (ReferenceEquals(viewEditor.State, session) && context.Matches(ViewEditorContext())) ShowViewError(ex.Message);
        }
        finally
        {
            viewWriteInProgress = false;
            saveViewSettingsButton.IsEnabled = true;
        }
    }

    private void ShowViewError(string message)
    {
        viewErrorText.Text = message;
        viewErrorText.IsVisible = true;
    }

    private Task DeleteSelectedViewAsync() => DeleteSelectedViewAsync(view =>
        KastnDialogs.ConfirmAsync(this, $"Delete the view '{view.Name}'? This cannot be undone.", "Delete"));

    private async Task DeleteSelectedViewAsync(Func<ZetlViewDocument, Task<bool>> confirm)
    {
        if (viewWriteInProgress) return;
        var view = ZetlViewDefaults.Clone(SelectedView);
        if (ZetlViewDefaults.IsBuiltIn(view.Id))
        {
            statusText.Text = "Built-in views can't be deleted.";
            return;
        }
        var context = ViewEditorContext();
        var projectScoped = viewCatalog.IsProjectScoped(view.Id);
        var editorSession = viewEditor.State;
        viewWriteInProgress = true;
        try
        {
            if (!await confirm(view) || !context.Matches(ViewEditorContext())) return;
            var result = await viewPersistence.DeleteAsync(view, projectScoped, context.Project,
                ExecuteMutationAsync, () => context.Matches(ViewEditorContext()));
            if (!context.Matches(ViewEditorContext()) || !ReferenceEquals(viewEditor.State, editorSession)) return;
            if (!result.Success)
            {
                statusText.Text = result.Error!;
                return;
            }
            await connection.SynchronizeAsync();
            if (!context.Matches(ViewEditorContext()) || !ReferenceEquals(viewEditor.State, editorSession)
                || connection.Current.Project?.Id != context.Project?.Id) return;
            var selectId = (viewPickerBox.SelectedItem as ZetlViewDocument)?.Id;
            ApplySnapshot(connection.Current);
            if (!context.Matches(ViewEditorContext())) return;
            RefreshViewCatalog(currentProject, selectId == view.Id ? null : selectId);
            RefreshViewer();
            statusText.Text = $"Deleted view '{view.Name}'.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            if (context.Matches(ViewEditorContext()) && ReferenceEquals(viewEditor.State, editorSession))
                statusText.Text = $"Could not delete view: {ex.Message}";
        }
        finally { viewWriteInProgress = false; }
    }
}
