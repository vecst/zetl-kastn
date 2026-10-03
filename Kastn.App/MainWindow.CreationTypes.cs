using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{    private void WireCreationEditor()
    {
        saveCreationButton.Click += (_, _) => SaveCreation();
        cancelCreationButton.Click += async (_, _) => await CancelCreationEditAsync();
    }

    private void RebuildCreationCards()
    {
        var loaded = creationStore.LoadAll();
        var templatesById = templateCatalog.LoadAll().ToDictionary(t => t.Id, StringComparer.Ordinal);
        creations.Clear();
        foreach (var creation in loaded)
        {
            var templateName = templatesById.TryGetValue(creation.TemplateId, out var t)
                ? t.Name
                : creation.TemplateId;
            var viewName = creation.PrimaryViewId is { } viewId
                ? globalViews.FirstOrDefault(v => v.Id == viewId)?.Name ?? viewId
                : "no view";
            creations.Add(new CreationListItem(
                creation.Category,
                creation.Name,
                $"Template: {templateName}  ·  View: {viewName}",
                creation,
                !ZetlCreationTypeDefaults.IsBuiltIn(creation.Id)));
        }

        RefreshLandingGridLayout();
    }

    private async void OnUseCreationClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is not CreationListItem creation)
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
        if ((sender as Control)?.DataContext is CreationListItem creation)
        {
            if (ZetlCreationTypeDefaults.IsBuiltIn(creation.Source.Id))
            {
                OpenCreationEditor(ZetlCreationTypeDefaults.Duplicate(creation.Source), isNew: true);
            }
            else
            {
                OpenCreationEditor(ZetlCreationTypeDefaults.Clone(creation.Source), isNew: false);
            }
        }
    }

    private void OnDuplicateCreationClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is CreationListItem creation)
        {
            OpenCreationEditor(ZetlCreationTypeDefaults.Duplicate(creation.Source), isNew: true);
        }
    }

    private async void OnDeleteCreationClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is not CreationListItem creation)
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

    private void OpenCreationEditor(ZetlCreationTypeDocument working, bool isNew)
    {
        editingCreation = working;
        creationErrorText.IsVisible = false;

        var templates = templateCatalog.LoadAll();
        var viewChoices = new List<ZetlViewDocument> { NoView };
        viewChoices.AddRange(viewStore.LoadAll());

        creationEditorTitle.Text = isNew ? "New Creation Type" : $"Edit Creation Type — {working.Name}";
        creationNameBox.Text = working.Name;
        creationCategoryBox.Text = working.Category;
        creationDescriptionBox.Text = working.Description;
        creationTemplateBox.ItemsSource = templates;
        creationTemplateBox.SelectedItem =
            templates.FirstOrDefault(t => t.Id == working.TemplateId) ?? templates.FirstOrDefault();
        creationViewBox.ItemsSource = viewChoices;
        creationViewBox.SelectedItem = working.PrimaryViewId is { } viewId
            ? viewChoices.FirstOrDefault(v => v.Id == viewId) ?? NoView
            : NoView;

        creationBaselineJson = CurrentCreationJson();

        emptyState.IsVisible = false;
        projectView.IsVisible = false;
        creationEditorView.IsVisible = true;
    }

    private string CurrentCreationJson()
    {
        if (editingCreation is null)
        {
            return "";
        }

        var doc = ZetlCreationTypeDefaults.Clone(editingCreation);
        doc.Name = creationNameBox.Text?.Trim() ?? "";
        doc.Category = string.IsNullOrWhiteSpace(creationCategoryBox.Text)
            ? "Custom"
            : creationCategoryBox.Text.Trim();
        doc.Description = creationDescriptionBox.Text?.Trim() ?? "";
        doc.TemplateId = (creationTemplateBox.SelectedItem as ZetlTemplateDocument)?.Id ?? "";
        var view = creationViewBox.SelectedItem as ZetlViewDocument;
        doc.ViewIds = view is null || string.IsNullOrEmpty(view.Id) ? [] : [view.Id];
        return JsonSerializer.Serialize(doc, JsonFile.Options);
    }

    private bool IsCreationDirty() =>
        editingCreation is not null && CurrentCreationJson() != creationBaselineJson;

    private async Task CancelCreationEditAsync()
    {
        if (IsCreationDirty())
        {
            var discard = await KastnDialogs.ConfirmAsync(
                this,
                "Discard unsaved changes to this creation type?",
                "Discard");
            if (!discard)
            {
                return;
            }
        }

        CloseCreationEditor();
    }

    private void SaveCreation()
    {
        if (editingCreation is not { } creation)
        {
            return;
        }

        creation.Name = creationNameBox.Text?.Trim() ?? "";
        creation.Category = string.IsNullOrWhiteSpace(creationCategoryBox.Text)
            ? "Custom"
            : creationCategoryBox.Text.Trim();
        creation.Description = creationDescriptionBox.Text?.Trim() ?? "";
        creation.TemplateId = (creationTemplateBox.SelectedItem as ZetlTemplateDocument)?.Id ?? "";
        var view = creationViewBox.SelectedItem as ZetlViewDocument;
        creation.ViewIds = view is null || string.IsNullOrEmpty(view.Id) ? [] : [view.Id];
        if (string.IsNullOrEmpty(creation.Id))
        {
            creation.Id = ZetlCreationTypeDefaults.CreateId(creation.Name);
        }

        var errors = ZetlCreationTypeValidator.Validate(creation);
        if (errors.Count > 0)
        {
            creationErrorText.Text = string.Join("\n", errors);
            creationErrorText.IsVisible = true;
            return;
        }

        try
        {
            creationStore.Save(creation);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
        {
            creationErrorText.Text = ex.Message;
            creationErrorText.IsVisible = true;
            return;
        }

        var savedName = creation.Name;
        CloseCreationEditor();
        if (currentProject is null)
        {
            landingSection = LandingSection.Creations;
            RebuildCreationCards();
            RefreshLandingMode();
        }

        statusText.Text = $"Saved creation type '{savedName}'.";
    }

    private void CloseCreationEditor()
    {
        editingCreation = null;
        creationErrorText.IsVisible = false;
        creationEditorView.IsVisible = false;
        if (currentProject is not null)
        {
            projectView.IsVisible = true;
            emptyState.IsVisible = false;
        }
        else
        {
            emptyState.IsVisible = true;
        }
    }

}
