using Avalonia.Controls;
using Avalonia.Interactivity;
using ZETL;

namespace KASTN;

internal partial class MainWindow
{
    private void WireCreationEditor()
    {
        saveCreationButton.Click += (_, _) => SaveCreation();
        cancelCreationButton.Click += async (_, _) => await CancelCreationEditAsync();
    }

    private async void OnUseCreationClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is not KastnCreationCard creation)
        {
            return;
        }

        if (!IsOnline)
        {
            statusText.Text = "Connect to Zetl before using a creation type.";
            return;
        }

        var template = templateCatalog.LoadAll()
            .FirstOrDefault(t => t.Id == creation.Source.TemplateId);
        if (template is null)
        {
            statusText.Text = $"The '{creation.Name}' template is missing.";
            return;
        }

        await CreateProjectFromTemplateAsync(template, creation.Source.PrimaryViewId);
    }

    private void OnNewCreationClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        OpenCreationEditor(new ZetlCreationTypeDocument { Name = "", Category = "Custom" }, isNew: true);
    }

    private void OnEditCreationClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is KastnCreationCard creation)
        {
            if (ZetlCreationTypeDefaults.IsBuiltIn(creation.Source.Id))
            {
                OpenCreationEditor(ZetlCreationTypeDefaults.Duplicate(creation.Source), isNew: true);
            }
            else
            {
                OpenCreationEditor(creation.Source, isNew: false);
            }
        }
    }

    private void OnDuplicateCreationClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is KastnCreationCard creation)
        {
            OpenCreationEditor(ZetlCreationTypeDefaults.Duplicate(creation.Source), isNew: true);
        }
    }

    private async void OnDeleteCreationClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is not KastnCreationCard creation)
        {
            return;
        }

        var confirmed = await KastnDialogs.ConfirmAsync(
            this,
            $"Delete the creation type '{creation.Name}'? This cannot be undone.",
            "Delete");
        if (!confirmed)
        {
            return;
        }

        try
        {
            creationStore.Delete(creation.Source.Id);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            statusText.Text = $"Could not delete creation type: {ex.Message}";
            return;
        }

        RebuildCreationCards();
        RefreshLandingMode();
        statusText.Text = $"Deleted creation type '{creation.Name}'.";
    }

    private KastnCreationEditorPresenter CreateCreationEditorPresenter() => new(new(
        creationEditorTitle, creationNameBox, creationCategoryBox, creationDescriptionBox,
        creationTemplateBox, creationViewBox));

    private void OpenCreationEditor(ZetlCreationTypeDocument working, bool isNew)
    {
        if (lifetime.IsRetired) return;
        viewEditor.Close();
        templateEditor.Close();
        viewEditorView.IsVisible = false;
        templateEditorView.IsVisible = false;
        creationEditor.Open(working, isNew, templateCatalog.LoadAll(), viewCatalog.Store.LoadAll(), NoView);
        creationErrorText.IsVisible = false;
        emptyState.IsVisible = false;
        projectView.IsVisible = false;
        creationEditorView.IsVisible = true;
    }

    private Task CancelCreationEditAsync() => CancelCreationEditAsync(() =>
        KastnDialogs.ConfirmAsync(this, "Discard unsaved changes to this creation type?", "Discard"));

    private async Task CancelCreationEditAsync(Func<Task<bool>> confirm)
    {
        if (creationEditor.State is not { } session || creationEditor.Capture() is not { } captured) return;
        var fingerprint = KastnCatalogEditorSession<ZetlCreationTypeDocument>.Fingerprint(captured);
        if (session.IsDirty(captured) && !await confirm()) return;
        if (!lifetime.IsRetired && ReferenceEquals(creationEditor.State, session)
            && creationEditor.Capture() is { } current
            && KastnCatalogEditorSession<ZetlCreationTypeDocument>.Fingerprint(current) == fingerprint)
            CloseCreationEditor();
    }

    private void SaveCreation()
    {
        if (lifetime.IsRetired || creationEditor.State is not { } session || creationEditor.Capture() is not { } captured) return;
        var result = session.Save(captured, creationStore.Save);
        if (result.Saved is not { } saved)
        {
            creationErrorText.Text = result.Error;
            creationErrorText.IsVisible = true;
            return;
        }
        CloseCreationEditor();
        if (currentProject is null)
        {
            ShowLandingSection(KastnLandingSection.Creations);
        }
        statusText.Text = $"Saved creation type '{saved.Name}'.";
    }

    private void CloseCreationEditor()
    {
        creationEditor.Close();
        creationErrorText.IsVisible = false;
        creationEditorView.IsVisible = false;
        projectView.IsVisible = currentProject is not null;
        emptyState.IsVisible = currentProject is null;
    }
}
