using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZETL;

namespace KASTN;

internal partial class MainWindow
{
    private readonly KastnLandingPage landing;

    private KastnLandingPage CreateLandingPage() => new(new(templateCatalog.LoadAll,
        creationStore.LoadAll, viewCatalog.Store.LoadAll), LaneLabel);

    private void ShowLandingSection(KastnLandingSection section, bool? consumable = null)
    {
        if (lifetime.IsRetired) return;
        ClearLandingSelection();
        landing.Visit(section, consumable);
        RefreshLandingMode();
    }

    private void SetTemplateType(bool consumable)
    {
        landing.SetTemplateType(consumable);
        RefreshLandingMode();
    }

    private void RebuildTemplateCards()
    {
        landing.ReloadTemplates();
        RefreshLandingGridLayout();
    }

    private void RebuildCreationCards()
    {
        landing.ReloadCreations();
        RefreshLandingGridLayout();
    }

    private void SetArchivedProjectMode(bool showArchived)
    {
        using var update = EnterUiUpdate();
        if (!landing.SetArchived(showArchived)) return;
        ClearLandingSelection();
        RefreshLandingMode();
    }

    private void ClearLandingSelection()
    {
        using var update = EnterUiUpdate();
        landingProjectList.SelectedItem = null;
        landingProjectWorkspaceList.SelectedItem = null;
    }

    private void RefreshLandingMode()
    {
        if (lifetime.IsRetired) return;
        var mode = landing.Presentation(emptyState.IsVisible && landingModeToggle.IsVisible, IsOnline);
        landingProjectsPanel.IsVisible = mode.Choices;
        landingLowerContent.IsVisible = mode.Choices;
        landingProjectLibraryHeader.IsVisible = mode.Choices;
        landingProjectList.IsVisible = mode.Choices && mode.HasRecents;
        landingProjectWorkspaceList.IsVisible = mode.Choices && mode.Projects && mode.HasProjects;
        landingTemplateList.IsVisible = mode.Choices && mode.Templates;
        landingTemplateList.IsEnabled = mode.Online;
        landingCreationList.IsVisible = mode.Choices && mode.Creations;
        landingCreationList.IsEnabled = mode.Online;
        landingLowerActionRow.IsVisible = mode.Choices;
        landingProjectArchiveToggle.IsVisible = mode.Choices && mode.Projects;
        landingTemplateTypeToggle.IsVisible = mode.Choices && mode.Templates;
        landingTemplateTypeToggle.IsEnabled = mode.Online;
        landingNewTemplateButton.IsVisible = mode.Choices && mode.Templates;
        landingNewCreationButton.IsVisible = mode.Choices && mode.Creations;
        landingWorkspaceTitle.Text = mode.Title;
        landingWorkspaceSubtitle.Text = mode.Subtitle;
        landingProjectsButton.IsEnabled = !mode.Projects;
        landingTemplatesButton.IsEnabled = !mode.Templates;
        landingCurrentProjectsButton.IsEnabled = mode.Archived;
        landingArchivedProjectsButton.IsEnabled = !mode.Archived;
        landingCreateButton.IsVisible = mode.Choices;
        landingCreateButton.IsEnabled = !mode.Creations;
        landingCaptureButton.IsEnabled = mode.Consumable;
        landingConsumableButton.IsEnabled = !mode.Consumable;
        RefreshLandingGridLayout();
    }

    private void RefreshLandingGridLayout()
    {
        if (lifetime.IsRetired) return;
        var contentWidth = Math.Max(560, Bounds.Width - 500);
        var lowerHeight = Math.Max(240, Bounds.Height - 210);
        landingProjectList.MaxHeight = lowerHeight;
        landingProjectWorkspaceList.Width = contentWidth;
        landingProjectWorkspaceList.MaxHeight = lowerHeight;
        landingTemplateList.Width = contentWidth;
        landingTemplateList.MaxHeight = lowerHeight;
        landingCreationList.Width = contentWidth;
        landingCreationList.MaxHeight = lowerHeight;
    }

    private string LaneLabel(string lane) =>
        settings.Current.LaneLabel(lane == ZetlStateRules.ShiftLane);

    private async void OnProjectSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (refreshing || lifetime.IsRetired) return;
        if (landing.ConsumeSuppressedSelection())
        {
            ClearLandingSelection();
            return;
        }

        if (sender is ListBox listBox
            && listBox.SelectedItem is KastnProjectCard project)
        {
            await OpenProjectCardAsync(project);
        }
    }

    private void OnProjectCardActionPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        var version = landing.BeginCardAction();
        Dispatcher.UIThread.Post(() => landing.EndCardAction(version), DispatcherPriority.Background);
    }

    private async void OnProjectCardOpenClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (ProjectFromControl(sender) is { } project)
        {
            await OpenProjectCardAsync(project);
        }
    }

    private async void OnProjectCardRenameClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (ProjectFromControl(sender) is { } project)
        {
            await RenameProjectAsync(project);
        }
    }

    private async void OnProjectCardPinClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (ProjectFromControl(sender) is { } project)
        {
            await SetActiveProjectAsync(project, shifted: false);
        }
    }

    private async void OnProjectCardSetAlternateClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (ProjectFromControl(sender) is { } project)
        {
            await SetActiveProjectAsync(project, shifted: true);
        }
    }

    private async void OnProjectCardDeleteClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (ProjectFromControl(sender) is { } project)
        {
            await DeleteProjectAsync(project);
        }
    }

    private async void OnProjectCardStatusClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (ProjectFromControl(sender) is { } project)
        {
            await SetProjectStatusAsync(project, project.StatusActionTarget);
        }
    }

    private async void OnProjectCardUseTemporarilyClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (ProjectFromControl(sender) is { } project)
        {
            await CreateTemporaryProjectFromReplayAsync(project);
        }
    }

    private static KastnProjectCard? ProjectFromControl(object? sender)
    {
        return sender is Control control
            ? control.Tag as KastnProjectCard ?? control.DataContext as KastnProjectCard
            : null;
    }

    private async Task OpenProjectCardAsync(KastnProjectCard project)
    {
        if (await navigation.NavigateProjectAsync(project.Id) != KastnProjectNavigationStatus.SaveBlocked) return;
        ClearLandingSelection();
    }
}
