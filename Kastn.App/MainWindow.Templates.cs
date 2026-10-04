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
{    private async void OnUseTemplateClick(object? sender, RoutedEventArgs args)
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
            // Edit a clone so cancelling leaves the saved file untouched.
            OpenTemplateEditor(ZetlTemplateDefaults.Clone(template.Source), isNew: false);
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

    // ---- In-window template editor (mirrors the project workbench) ----

    private void WireTemplateEditor()
    {
        templateBucketList.ItemsSource = templateBuckets;
        templateTypeBox.ItemsSource = TemplateTypeChoices;
        templateBucketKindBox.ItemsSource = TemplateKindChoices;
        templateBucketCompileBox.ItemsSource = TemplateCompileChoices;

        saveTemplateButton.Click += (_, _) => SaveTemplate();
        cancelTemplateButton.Click += async (_, _) => await CancelTemplateEditAsync();
        saveAsTemplateMenuItem.Click += (_, _) => OpenTemplateFromProject();

        templateAddBucketButton.Click += (_, _) => AddTemplateBucket();
        templateDeleteBucketButton.Click += (_, _) => DeleteTemplateBucket();
        templateBucketUpButton.Click += (_, _) => MoveTemplateBucket(-1);
        templateBucketDownButton.Click += (_, _) => MoveTemplateBucket(1);
        templateBucketList.SelectionChanged += (_, _) => OnTemplateBucketSelected();

        templateTypeBox.SelectionChanged += (_, _) =>
        {
            if (!templateEditorUpdating)
            {
                ApplyTemplateSeedsVisibility();
            }
        };
        templateTemporaryBox.IsCheckedChanged += (_, _) =>
        {
            if (!templateEditorUpdating && !IsTemplateConsumableSelection())
            {
                templateTemporaryBox.IsChecked = false;
            }
        };
        templateBucketNameBox.TextChanged += (_, _) => CommitBucketFields(renamed: true);
        templateBucketKindBox.SelectionChanged += (_, _) => CommitBucketFields();
        templateBucketCompileBox.SelectionChanged += (_, _) => CommitBucketFields();
        templateBucketTsvBox.ValueChanged += (_, _) => CommitBucketFields();
        templateBucketStartBox.TextChanged += (_, _) => CommitBucketFields();
        templateBucketSeedsBox.TextChanged += (_, _) => CommitBucketFields();
    }

    // Open the editor on a working document (a draft, clone, or duplicate). The
    // document is mutated in place as fields change; cancelling simply discards it.
    private void OpenTemplateEditor(ZetlTemplateDocument working, bool isNew)
    {
        editingTemplate = working;
        templateErrorText.IsVisible = false;

        templateEditorUpdating = true;
        templateEditorTitle.Text = isNew ? "New Template" : $"Edit Template — {working.Name}";
        templateNameBox.Text = working.Name;
        templateCategoryBox.Text = working.Category;
        templateDescriptionBox.Text = working.Description;
        templateTypeBox.SelectedItem = working.IsConsumable
            ? ZetlTemplateTypes.Consumable
            : ZetlTemplateTypes.Capture;
        templateTemporaryBox.IsChecked = working.Temporary && working.IsConsumable;
        templateEditorUpdating = false;

        if (working.Buckets.Count == 0)
        {
            working.Buckets.Add(new ZetlTemplateBucketDocument { Name = "Inbox" });
        }

        RebuildTemplateBucketList(selectIndex: 0);
        ApplyTemplateSeedsVisibility();
        templateBaselineJson = CurrentTemplateJson();

        emptyState.IsVisible = false;
        projectView.IsVisible = false;
        templateEditorView.IsVisible = true;
    }

    // Serialize the working document with the current metadata-field values applied,
    // so a baseline taken at open and a later snapshot compare apples to apples.
    private string CurrentTemplateJson()
    {
        if (editingTemplate is null)
        {
            return "";
        }

        var consumable = IsTemplateConsumableSelection();
        var doc = ZetlTemplateDefaults.Clone(editingTemplate);
        doc.Name = templateNameBox.Text?.Trim() ?? "";
        doc.Category = string.IsNullOrWhiteSpace(templateCategoryBox.Text)
            ? "Custom"
            : templateCategoryBox.Text.Trim();
        doc.Description = templateDescriptionBox.Text?.Trim() ?? "";
        doc.Type = consumable ? ZetlTemplateTypes.Consumable : ZetlTemplateTypes.Capture;
        doc.Temporary = consumable && templateTemporaryBox.IsChecked == true;
        return JsonSerializer.Serialize(doc, JsonFile.Options);
    }

    private bool IsTemplateDirty() =>
        editingTemplate is not null && CurrentTemplateJson() != templateBaselineJson;

    private async Task CancelTemplateEditAsync()
    {
        if (IsTemplateDirty())
        {
            var discard = await KastnDialogs.ConfirmAsync(
                this,
                "Discard unsaved changes to this template?",
                "Discard");
            if (!discard)
            {
                return;
            }
        }

        CloseTemplateEditor();
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

    private void RebuildTemplateBucketList(int selectIndex)
    {
        templateBuckets.Clear();
        if (editingTemplate is null)
        {
            return;
        }

        foreach (var bucket in editingTemplate.Buckets)
        {
            templateBuckets.Add(new TemplateBucketItem(bucket, BucketLabel(bucket)));
        }

        if (templateBuckets.Count > 0)
        {
            templateBucketList.SelectedIndex = Math.Clamp(selectIndex, 0, templateBuckets.Count - 1);
        }
        else
        {
            OnTemplateBucketSelected();
        }
    }

    private void OnTemplateBucketSelected()
    {
        selectedTemplateBucket =
            (templateBucketList.SelectedItem as TemplateBucketItem)?.Bucket;
        var bucket = selectedTemplateBucket;
        templateBucketEditor.IsVisible = bucket is not null;
        templateBucketEmptyHint.IsVisible = bucket is null;
        if (bucket is null)
        {
            return;
        }

        templateEditorUpdating = true;
        templateBucketNameBox.Text = bucket.Name;
        templateBucketKindBox.SelectedItem = TemplateKindChoices.Contains(bucket.Settings.Kind)
            ? bucket.Settings.Kind
            : "Standard";
        templateBucketCompileBox.SelectedItem =
            TemplateCompileChoices.Contains(bucket.Settings.DefaultCompileMode)
                ? bucket.Settings.DefaultCompileMode
                : "Formatted";
        templateBucketTsvBox.Value = Math.Clamp(bucket.Settings.DefaultTsvRowLength, 1, 100);
        templateBucketStartBox.Text = bucket.Settings.DefaultStartingText;
        templateBucketSeedsBox.Text = FormatTemplateCards(bucket);
        templateEditorUpdating = false;
    }

    // Write the right-panel fields back into the selected bucket document.
    private void CommitBucketFields(bool renamed = false)
    {
        if (templateEditorUpdating || selectedTemplateBucket is not { } bucket)
        {
            return;
        }

        var kind = templateBucketKindBox.SelectedItem as string ?? "Standard";
        bucket.Name = templateBucketNameBox.Text?.Trim() ?? "";
        bucket.Settings = new ZETL.ZetlBucketSettings
        {
            Kind = kind,
            DefaultKind = kind,
            DefaultCompileMode = templateBucketCompileBox.SelectedItem as string ?? "Formatted",
            DefaultStartingText = templateBucketStartBox.Text ?? "",
            DefaultTsvRowLength = (int)(templateBucketTsvBox.Value ?? 5)
        };
        bucket.Seeds = [];
        bucket.Cards = ParseTemplateCards(templateBucketSeedsBox.Text);

        if (renamed)
        {
            var item = templateBuckets.FirstOrDefault(
                entry => ReferenceEquals(entry.Bucket, bucket));
            if (item is not null)
            {
                item.Label = BucketLabel(bucket);
            }
        }
    }

    private void AddTemplateBucket()
    {
        if (editingTemplate is null)
        {
            return;
        }

        var bucket = new ZetlTemplateBucketDocument { Name = "" };
        editingTemplate.Buckets.Add(bucket);
        templateBuckets.Add(new TemplateBucketItem(bucket, BucketLabel(bucket)));
        templateBucketList.SelectedIndex = templateBuckets.Count - 1;
        templateBucketNameBox.Focus();
    }

    private void DeleteTemplateBucket()
    {
        if (editingTemplate is null || selectedTemplateBucket is not { } bucket)
        {
            return;
        }

        if (editingTemplate.Buckets.Count <= 1)
        {
            ShowTemplateError("A template needs at least one bucket.");
            return;
        }

        var index = editingTemplate.Buckets.IndexOf(bucket);
        editingTemplate.Buckets.Remove(bucket);
        RebuildTemplateBucketList(selectIndex: Math.Max(0, index - 1));
    }

    private void MoveTemplateBucket(int delta)
    {
        if (editingTemplate is null || selectedTemplateBucket is not { } bucket)
        {
            return;
        }

        var index = editingTemplate.Buckets.IndexOf(bucket);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= editingTemplate.Buckets.Count)
        {
            return;
        }

        editingTemplate.Buckets.RemoveAt(index);
        editingTemplate.Buckets.Insert(target, bucket);
        RebuildTemplateBucketList(selectIndex: target);
    }

    private void ApplyTemplateSeedsVisibility()
    {
        var consumable = IsTemplateConsumableSelection();
        templateTemporaryBox.IsEnabled = consumable;
        if (!consumable)
        {
            templateTemporaryBox.IsChecked = false;
        }

        templateSeedsPanel.IsVisible = true;
    }

    private void SaveTemplate()
    {
        if (editingTemplate is not { } template)
        {
            return;
        }

        var consumable = IsTemplateConsumableSelection();
        template.Name = templateNameBox.Text?.Trim() ?? "";
        template.Category = string.IsNullOrWhiteSpace(templateCategoryBox.Text)
            ? "Custom"
            : templateCategoryBox.Text.Trim();
        template.Description = templateDescriptionBox.Text?.Trim() ?? "";
        template.Type = consumable ? ZetlTemplateTypes.Consumable : ZetlTemplateTypes.Capture;
        template.Temporary = consumable && templateTemporaryBox.IsChecked == true;
        if (string.IsNullOrEmpty(template.Id))
        {
            template.Id = ZetlTemplateDefaults.CreateId(template.Name);
        }

        var errors = ZetlTemplateValidator.Validate(template);
        if (errors.Count > 0)
        {
            ShowTemplateError(string.Join("\n", errors));
            return;
        }

        try
        {
            templateCatalog.Store.Save(template);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
        {
            ShowTemplateError(ex.Message);
            return;
        }

        var savedName = template.Name;
        var savedConsumable = template.IsConsumable;
        CloseTemplateEditor();
        // After saving, surface the templates tab on the matching type so the new
        // card is visible (unless a project is open, where Close returns there).
        if (currentProject is null)
        {
            landingSection = LandingSection.Templates;
            landingShowingConsumable = savedConsumable;
            RebuildTemplateCards();
            RefreshLandingMode();
        }

        statusText.Text = $"Saved template '{savedName}'.";
    }

    private void CloseTemplateEditor()
    {
        editingTemplate = null;
        selectedTemplateBucket = null;
        templateBuckets.Clear();
        templateErrorText.IsVisible = false;
        templateEditorView.IsVisible = false;

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

    private void ShowTemplateError(string message)
    {
        templateErrorText.Text = message;
        templateErrorText.IsVisible = true;
    }

    private static string BucketLabel(ZetlTemplateBucketDocument bucket) =>
        string.IsNullOrWhiteSpace(bucket.Name) ? "(unnamed bucket)" : bucket.Name;

    private bool IsTemplateConsumableSelection() =>
        (templateTypeBox.SelectedItem as string) == ZetlTemplateTypes.Consumable;

    private static List<ZetlTemplateSlipDocument> ParseTemplateCards(string? text) =>
        (text ?? "")
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line =>
            {
                var separator = line.IndexOf("::", StringComparison.Ordinal);
                return separator < 0
                    ? new ZetlTemplateSlipDocument { Text = line }
                    : new ZetlTemplateSlipDocument
                    {
                        Title = line[..separator].Trim(),
                        Text = line[(separator + 2)..].Trim()
                    };
            })
            .ToList();

    private static string FormatTemplateCards(ZetlTemplateBucketDocument bucket)
    {
        var lines = bucket.Seeds.ToList();
        lines.AddRange(bucket.Cards.Select(card => string.IsNullOrWhiteSpace(card.Title)
            ? card.Text
            : $"{card.Title} :: {card.Text}".TrimEnd()));
        return string.Join("\n", lines);
    }

    private async Task CreateProjectFromTemplateAsync(
        ZetlTemplateDocument template,
        string? defaultViewId = null)
    {
        if (!IsOnline)
        {
            statusText.Text = "Connect to Zetl before creating a project from a template.";
            return;
        }

        var prompt = await KastnDialogs.PromptTemplateProjectAsync(
            this,
            $"New {template.Name} Project",
            template.Name,
            allowTemporary: template.IsConsumable,
            initialTemporary: template.Temporary,
            candidate => projects.Any(project =>
                string.Equals(project.Name, candidate, StringComparison.OrdinalIgnoreCase))
                ? "A project with that name already exists."
                : null);
        if (prompt is null)
        {
            return;
        }

        var useTemporary = template.IsConsumable && prompt.Temporary;
        var temporaryLane = await ResolveTemporaryTemplateLaneAsync(template, useTemporary);
        if (useTemporary && temporaryLane is null)
        {
            return;
        }

        var create = template.ToCreateProjectCommand(
            prompt.Name,
            temporaryLane,
            temporary: useTemporary);
        create = create with
        {
            ActivateShifted = string.Equals(temporaryLane, ZetlStateRules.ShiftLane, StringComparison.Ordinal)
        };

        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.CreateProject,
            create));
        if (response.Status == ZetlResponseStatus.Success)
        {
            var created = response.Payload?.Deserialize<ZetlProjectSnapshot>(
                ZetlProtocolJson.Options);
            if (created is not null)
            {
                var allSeeded = await SeedTemplateSlipsAsync(template, created);
                if (!string.IsNullOrEmpty(defaultViewId))
                {
                    await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                        Guid.NewGuid().ToString("N"),
                        ZetlCommandKind.SetProjectView,
                        new SetProjectViewCommand { ViewId = defaultViewId },
                        created.Id,
                        expectedTargetRevision: created.MetadataRevision));
                }

                await connection.NavigateToProjectAsync(created.Id);
                statusText.Text = allSeeded
                    ? $"Created '{created.Name}' from the {template.Name} template."
                    : $"Created '{created.Name}', but some {template.Name} fields could not be added.";
                // Templates kick off a capture session: step Kastn aside (if the user
                // opted in) so Zetl's capture is unobstructed. The project stays loaded
                // and ready for when they return to the workbench.
                if (this.settings.Current.KastnMinimizeAfterTemplate)
                {
                    WindowState = WindowState.Minimized;
                }

                return;
            }
        }

        HandleSimpleResponse(response, $"Created a project from the {template.Name} template.");
    }

    private async Task<string?> ResolveTemporaryTemplateLaneAsync(
        ZetlTemplateDocument template,
        bool useTemporary)
    {
        if (!useTemporary || !template.IsConsumable)
        {
            return null;
        }

        var configured = ZetlKastnTemplateLaneDefault.Normalize(
            settings.Current.KastnTemporaryTemplateLaneDefault);
        if (configured.Length > 0)
        {
            return configured;
        }

        var choice = await KastnDialogs.PickTemporaryTemplateLaneAsync(
            this,
            template.Name,
            LaneLabel(ZetlStateRules.NormalLane),
            LaneLabel(ZetlStateRules.ShiftLane));
        if (choice is null)
        {
            return null;
        }

        var lane = ZetlStateRules.CanonicalTemporaryLane(choice.Lane)
            ?? ZetlStateRules.NormalLane;
        if (choice.Remember)
        {
            settings.RememberTemporaryTemplateLane(lane);
        }

        return lane;
    }

    // Seed template slips through Zetl in listed order, preserving Replay order.
    // Buckets without seed text or cards need no commands.
    private async Task<bool> SeedTemplateSlipsAsync(
        ZetlTemplateDocument template,
        ZetlProjectSnapshot project)
    {
        var allSeeded = true;
        foreach (var bucket in template.Buckets)
        {
            if (bucket.Seeds.Count == 0 && bucket.Cards.Count == 0)
            {
                continue;
            }

            var target = project.Buckets.FirstOrDefault(
                item => string.Equals(item.Name, bucket.Name, StringComparison.Ordinal));
            if (target is null)
            {
                allSeeded = false;
                continue;
            }

            var cards = bucket.Seeds
                .Select(text => new ZetlTemplateSlipDocument { Text = text })
                .Concat(bucket.Cards);
            foreach (var card in cards)
            {
                var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                    Guid.NewGuid().ToString("N"),
                    ZetlCommandKind.AddSlip,
                    new AddSlipCommand
                    {
                        BucketId = target.Id,
                        Title = card.Title,
                        Text = card.Text,
                        Source = "template"
                    },
                    project.Id));
                if (response.Status != ZetlResponseStatus.Success)
                {
                    allSeeded = false;
                }
            }
        }

        return allSeeded;
    }
}
