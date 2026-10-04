using Avalonia.Controls;
using Avalonia.Interactivity;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    private async void OnUseTemplateClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is TemplateListItem template)
        {
            await CreateProjectFromTemplateAsync(template.Source);
        }
    }

    private void OnNewTemplateClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        OpenTemplateEditor(ZetlTemplateDefaults.CreateDraft(), isNew: true);
    }

    private void OnEditTemplateClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is not TemplateListItem template)
        {
            return;
        }

        if (ZetlTemplateDefaults.IsBuiltIn(template.Source.Id))
        {
            // Built-ins are immutable presets: "Edit" forks an editable copy (new id
            // + name) so the preset stays intact, matching the creation-type cards.
            OpenTemplateEditor(ZetlTemplateDefaults.Duplicate(template.Source), isNew: true);
        }
        else
        {
            OpenTemplateEditor(template.Source, isNew: false);
        }
    }

    private void OnDuplicateTemplateClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is TemplateListItem template)
        {
            // A fresh id + name, so duplicating a built-in yields an editable copy
            // and the original preset stays immutable.
            OpenTemplateEditor(ZetlTemplateDefaults.Duplicate(template.Source), isNew: true);
        }
    }

    private async void OnDeleteTemplateClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is not TemplateListItem template)
        {
            return;
        }

        var confirmed = await KastnDialogs.ConfirmAsync(
            this,
            $"Delete the template '{template.Name}'? This cannot be undone.",
            "Delete");
        if (!confirmed)
        {
            return;
        }

        try
        {
            templateCatalog.Store.Delete(template.Source.Id);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            statusText.Text = $"Could not delete template: {ex.Message}";
            return;
        }

        RebuildTemplateCards();
        RefreshLandingMode();
        statusText.Text = $"Deleted template '{template.Name}'.";
    }

    private KastnTemplateEditorPresenter CreateTemplateEditorPresenter() => new(new(
        templateEditorTitle, templateNameBox, templateCategoryBox, templateDescriptionBox,
        templateTypeBox, templateTemporaryBox, templateBucketList, templateBucketEditor,
        templateBucketEmptyHint, templateBucketNameBox, templateBucketKindBox,
        templateBucketCompileBox, templateBucketTsvBox, templateBucketStartBox,
        templateBucketSeedsBox, templateSeedsPanel, templateAddBucketButton,
        templateDeleteBucketButton, templateBucketUpButton, templateBucketDownButton), ShowTemplateError);

    private void WireTemplateEditor()
    {
        saveTemplateButton.Click += (_, _) => SaveTemplate();
        cancelTemplateButton.Click += async (_, _) => await CancelTemplateEditAsync();
        saveAsTemplateMenuItem.Click += (_, _) => OpenTemplateFromProject();
    }

    private void OpenTemplateEditor(ZetlTemplateDocument working, bool isNew)
    {
        if (lifetime.IsRetired) return;
        viewEditor.Close();
        creationEditor.Close();
        viewEditorView.IsVisible = false;
        creationEditorView.IsVisible = false;
        templateEditor.Open(working, isNew);
        templateErrorText.IsVisible = false;
        emptyState.IsVisible = false;
        projectView.IsVisible = false;
        templateEditorView.IsVisible = true;
    }

    private Task CancelTemplateEditAsync() => CancelTemplateEditAsync(() =>
        KastnDialogs.ConfirmAsync(this, "Discard unsaved changes to this template?", "Discard"));

    private async Task CancelTemplateEditAsync(Func<Task<bool>> confirm)
    {
        if (templateEditor.State is not { } session || templateEditor.Capture() is not { } captured) return;
        var fingerprint = KastnCatalogEditorSession<ZetlTemplateDocument>.Fingerprint(captured);
        if (session.IsDirty(captured) && !await confirm()) return;
        if (!lifetime.IsRetired && ReferenceEquals(templateEditor.State, session)
            && templateEditor.Capture() is { } current
            && KastnCatalogEditorSession<ZetlTemplateDocument>.Fingerprint(current) == fingerprint)
            CloseTemplateEditor();
    }

    private void SaveTemplate()
    {
        if (lifetime.IsRetired || templateEditor.State is not { } session || templateEditor.Capture() is not { } captured) return;
        var result = session.Save(captured, templateCatalog.Store.Save);
        if (result.Saved is not { } saved) { ShowTemplateError(result.Error!); return; }
        CloseTemplateEditor();
        if (currentProject is null)
        {
            landingSection = LandingSection.Templates;
            landingShowingConsumable = saved.IsConsumable;
            RebuildTemplateCards();
            RefreshLandingMode();
        }
        statusText.Text = $"Saved template '{saved.Name}'.";
    }

    private void CloseTemplateEditor()
    {
        templateEditor.Close();
        templateErrorText.IsVisible = false;
        templateEditorView.IsVisible = false;
        projectView.IsVisible = currentProject is not null;
        emptyState.IsVisible = currentProject is null;
    }

    private void ShowTemplateError(string message)
    {
        templateErrorText.Text = message;
        templateErrorText.IsVisible = true;
    }

    private void OpenTemplateFromProject()
    {
        if (currentProject is not { } project)
        {
            return;
        }

        // Grab the project's bucket structure and text cards. Optional titles let
        // a project act as a worksheet without putting its labels in note text.
        var buckets = project.Buckets
            .Where(bucket => !ZetlTemplateValidator.ReservedName(bucket.Name))
            .Select(bucket => new ZetlTemplateBucketDocument
            {
                Name = bucket.Name,
                Settings = new ZETL.ZetlBucketSettings
                {
                    Kind = bucket.Settings.DefaultKind,
                    DefaultKind = bucket.Settings.DefaultKind,
                    DefaultCompileMode = bucket.Settings.DefaultCompileMode,
                    DefaultStartingText = bucket.Settings.DefaultStartingText,
                    DefaultTsvRowLength = bucket.Settings.DefaultTsvRowLength
                },
                Cards = project.Slips
                    .Where(slip => slip.BucketId == bucket.Id
                        && slip.Type != ZetlSlipType.Picture
                        && (!string.IsNullOrWhiteSpace(slip.Title)
                            || !string.IsNullOrWhiteSpace(slip.Text)))
                    .Select(slip => new ZetlTemplateSlipDocument
                    {
                        Title = slip.Title,
                        Text = slip.Text
                    })
                    .ToList()
            })
            .ToList();
        if (buckets.Count == 0)
        {
            buckets.Add(new ZetlTemplateBucketDocument { Name = "Inbox" });
        }

        OpenTemplateEditor(
            new ZetlTemplateDocument
            {
                Name = $"{project.Name} template",
                Category = "Custom",
                Type = ZetlTemplateTypes.Capture,
                Buckets = buckets
            },
            isNew: true);
    }
}
