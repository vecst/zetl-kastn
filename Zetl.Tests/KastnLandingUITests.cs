using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaFact]
    public void UnchangedLandingSnapshotKeepsNativeCardsAndFocusWithoutStartingNavigation()
    {
        var window = CatalogEditorWindow();
        var controller = WindowField<KastnConnectionController>(window, "connection");
        try
        {
            var summaries = new[] { KastnLandingPageTests.Project("alpha"), KastnLandingPageTests.Project("beta") };
            PublishLanding(window, controller, summaries);
            window.UpdateLayout();
            var card = Assert.IsType<ListBoxItem>(window.landingProjectWorkspaceList.ContainerFromIndex(0));
            var button = card.GetVisualDescendants().OfType<Button>().First();
            Assert.True(button.Focus());
            Dispatcher.UIThread.RunJobs();
            PublishLanding(window, controller, summaries.Select(project => project with { }).ToArray());
            window.UpdateLayout();
            Assert.Same(card, window.landingProjectWorkspaceList.ContainerFromIndex(0));
            Assert.True(button.IsFocused);
            Assert.Equal(0, controller.NavigationVersion);
            Assert.Null(window.landingProjectWorkspaceList.SelectedItem);
            Assert.Null(window.landingProjectList.SelectedItem);
            window.landingProjectWorkspaceList.SelectedItem = window.landingProjectWorkspaceList.ItemsSource!.Cast<KastnProjectCard>().First();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, controller.NavigationVersion); // Genuine list selection still opens a project.
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void ArchivedOnlyLandingStillExposesItsLibraryAndPreservesArchiveModeAcrossTabsAndSnapshots()
    {
        var window = CatalogEditorWindow();
        var controller = WindowField<KastnConnectionController>(window, "connection");
        try
        {
            var archived = KastnLandingPageTests.Project("archived") with { Status = "Archived" };
            PublishLanding(window, controller, [archived]);
            Assert.True(window.landingProjectArchiveToggle.IsVisible);
            Assert.False(window.landingProjectWorkspaceList.IsVisible);
            ClickLanding(window.landingArchivedProjectsButton);
            Assert.True(window.landingProjectWorkspaceList.IsVisible);
            Assert.Equal("archived", Assert.Single(window.landingProjectWorkspaceList.ItemsSource!.Cast<KastnProjectCard>()).Id);
            ClickLanding(window.landingTemplatesButton);
            Assert.True(window.landingTemplateList.IsVisible);
            ClickLanding(window.landingProjectsButton);
            PublishLanding(window, controller, [archived with { MetadataRevision = 2 }]);
            Assert.True(window.landingProjectWorkspaceList.IsVisible);
            Assert.False(window.landingArchivedProjectsButton.IsEnabled);
            Assert.Equal(2, Assert.Single(window.landingProjectWorkspaceList.ItemsSource!.Cast<KastnProjectCard>()).MetadataRevision);
            PublishLanding(window, controller, [archived], KastnConnectionState.Offline);
            Assert.False(window.landingProjectsPanel.IsVisible);
            Assert.False(window.landingTemplateList.IsEnabled);
            Assert.False(window.landingCreationList.IsEnabled);
            PublishLanding(window, controller, [archived]);
            Assert.True(window.landingProjectWorkspaceList.IsVisible);
            Assert.Equal(0, controller.NavigationVersion);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void LandingVisitsReloadExternalCatalogChangesAndPreserveConsumableMode()
    {
        var window = CatalogEditorWindow();
        var controller = WindowField<KastnConnectionController>(window, "connection");
        try
        {
            var templateStore = WindowField<KastnTemplateCatalog>(window, "templateCatalog").Store;
            var creationStore = WindowField<ZetlCreationTypeStore>(window, "creationStore");
            var viewStore = WindowField<KastnViewCatalog>(window, "viewCatalog").Store;
            var template = KastnProjectCreationTests.Template();
            templateStore.Save(template);
            var view = new ZetlViewDocument { Id = "landing-view", Name = "Original view", Kind = ZetlViewKinds.Markdown };
            viewStore.Save(view);
            creationStore.Save(new() { Id = "landing-starter", Name = "Landing starter", TemplateId = template.Id, ViewIds = [view.Id] });
            PublishLanding(window, controller, []);
            ClickLanding(window.landingCreateButton);
            var owner = WindowField<KastnLandingPage>(window, "landing");
            var original = owner.Creations.Single(card => card.Source.Id == "landing-starter");
            Assert.Contains("Original view", original.Detail);
            view.Name = "Renamed externally";
            viewStore.Save(view);
            ClickLanding(window.landingProjectsButton);
            ClickLanding(window.landingCreateButton);
            Assert.Contains("Renamed externally", owner.Creations.Single(card => card.Source.Id == "landing-starter").Detail);
            ClickLanding(window.landingTemplatesButton);
            ClickLanding(window.landingConsumableButton);
            Assert.Contains(owner.Templates, card => card.Source.Id == template.Id);
            ClickLanding(window.landingProjectsButton);
            templateStore.Delete(template.Id);
            ClickLanding(window.landingTemplatesButton);
            Assert.True(owner.ShowingConsumable);
            Assert.DoesNotContain(owner.Templates, card => card.Source.Id == template.Id);
            Assert.False(window.landingConsumableButton.IsEnabled);
            Assert.Equal(0, controller.NavigationVersion);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void CardActionSuppressionSurvivesProgrammaticSelectionClearAndNestedUpdateGuards()
    {
        var window = CatalogEditorWindow();
        var controller = WindowField<KastnConnectionController>(window, "connection");
        try
        {
            var summaries = new[] { KastnLandingPageTests.Project("alpha"), KastnLandingPageTests.Project("beta") };
            PublishLanding(window, controller, summaries);
            var owner = WindowField<KastnLandingPage>(window, "landing");
            var currentAction = owner.BeginCardAction();
            typeof(MainWindow).GetField("refreshing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            try
            {
                window.landingProjectWorkspaceList.SelectedItem = owner.Projects[0];
                ClickLanding(window.landingArchivedProjectsButton);
                Assert.True(WindowField<bool>(window, "refreshing"));
            }
            finally { typeof(MainWindow).GetField("refreshing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false); }
            ClickLanding(window.landingCurrentProjectsButton);
            window.landingProjectWorkspaceList.SelectedItem = owner.Projects[0];
            Assert.Null(window.landingProjectWorkspaceList.SelectedItem);
            Assert.Equal(0, controller.NavigationVersion);
            owner.EndCardAction(currentAction);
            window.landingProjectWorkspaceList.SelectedItem = owner.Projects[1];
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, controller.NavigationVersion);
        }
        finally { CloseWindow(window); }
    }

    private static void ClickLanding(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void PublishLanding(MainWindow window, KastnConnectionController controller,
        IReadOnlyList<ZetlProjectSummary> summaries, KastnConnectionState state = KastnConnectionState.Online)
    {
        var snapshot = new KastnSessionSnapshot(state, "Landing", summaries, null);
        typeof(KastnConnectionController).GetProperty(nameof(KastnConnectionController.Current))!.SetValue(controller, snapshot);
        typeof(MainWindow).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [snapshot]);
        Dispatcher.UIThread.RunJobs();
    }
}
