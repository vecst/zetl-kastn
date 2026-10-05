using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    private MainWindow CatalogEditorWindow(string? templateDirectory = null, string? creationDirectory = null)
    {
        var window = new MainWindow(new KastnConnectionController(_ => Task.CompletedTask),
            viewStore: new ZetlViewStore(Path.Combine(defaultDraftDirectory, "views")),
            templateStore: new ZetlTemplateStore(templateDirectory ?? Path.Combine(defaultDraftDirectory, "templates")),
            creationStore: new ZetlCreationTypeStore(creationDirectory ?? Path.Combine(defaultDraftDirectory, "creations")));
        window.Show();
        return window;
    }

    private static void OpenCatalogEditor(MainWindow window, string kind)
    {
        if (kind == "template")
        {
            var source = KastnProjectCreationTests.Template();
            typeof(MainWindow).GetMethod("OpenTemplateEditor", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [source, true]);
        }
        else
        {
            var source = new ZetlCreationTypeDocument { Id = "mine", Name = "Starter", TemplateId = "blank", ViewIds = ["markdown"] };
            typeof(MainWindow).GetMethod("OpenCreationEditor", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [source, true]);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static TextBox CatalogName(MainWindow window, string kind) => kind == "template" ? window.templateNameBox : window.creationNameBox;
    private static Control CatalogView(MainWindow window, string kind) => kind == "template" ? window.templateEditorView : window.creationEditorView;

    [AvaloniaTheory]
    [InlineData("template")]
    [InlineData("creation")]
    public void EditingBuiltInCatalogCardsPersistsAnIndependentCopy(string kind)
    {
        var window = CatalogEditorWindow();
        try
        {
            object source = kind == "template" ? ZetlTemplateDefaults.CreateAll().First() : ZetlCreationTypeDefaults.CreateAll().First();
            var original = JsonSerializer.Serialize(source, source.GetType(), JsonFile.Options);
            object item = kind == "template"
                ? new KastnTemplateCard("Built-in", "Preset", "", (ZetlTemplateDocument)source, false)
                : new KastnCreationCard("Built-in", "Preset", "", (ZetlCreationTypeDocument)source, false);
            typeof(MainWindow).GetMethod(kind == "template" ? "OnEditTemplateClick" : "OnEditCreationClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [new Button { DataContext = item }, new RoutedEventArgs(Button.ClickEvent)]);
            CatalogName(window, kind).Text = "My independent copy";
            (kind == "template" ? window.saveTemplateButton : window.saveCreationButton).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(CatalogView(window, kind).IsVisible);
            Assert.Equal(original, JsonSerializer.Serialize(source, source.GetType(), JsonFile.Options));
            if (kind == "template")
            {
                var saved = new ZetlTemplateStore(Path.Combine(defaultDraftDirectory, "templates")).LoadAll().Single(doc => doc.Name == "My independent copy");
                Assert.False(ZetlTemplateDefaults.IsBuiltIn(saved.Id));
            }
            else
            {
                var saved = new ZetlCreationTypeStore(Path.Combine(defaultDraftDirectory, "creations")).LoadAll().Single(doc => doc.Name == "My independent copy");
                Assert.False(ZetlCreationTypeDefaults.IsBuiltIn(saved.Id));
            }
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData("template")]
    [InlineData("creation")]
    public async Task DecliningCatalogDiscardRetainsWriting(string kind)
    {
        var window = CatalogEditorWindow();
        try
        {
            OpenCatalogEditor(window, kind);
            CatalogName(window, kind).Text = "Keep writing";
            await InvokeWorkflow(window, kind == "template" ? "CancelTemplateEditAsync" : "CancelCreationEditAsync",
                (Func<Task<bool>>)(() => Task.FromResult(false)));
            Assert.True(CatalogView(window, kind).IsVisible);
            Assert.Equal("Keep writing", CatalogName(window, kind).Text);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void TemplateBucketAddDeleteCapturesWritingAndProtectsTheLastBucket()
    {
        var window = CatalogEditorWindow();
        try
        {
            OpenCatalogEditor(window, "template");
            window.templateBucketNameBox.Text = "Retained first";
            window.templateAddBucketButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.templateBucketNameBox.Text = "Added";
            window.templateBucketSeedsBox.Text = "New card";
            window.templateBucketList.SelectedIndex = 1;
            window.templateDeleteBucketButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.templateBucketList.SelectedIndex = 0;
            window.templateDeleteBucketButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.templateDeleteBucketButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.templateErrorText.IsVisible);
            window.saveTemplateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved = new ZetlTemplateStore(Path.Combine(defaultDraftDirectory, "templates")).LoadAll().Single(doc => doc.Id == "test-template");
            var bucket = Assert.Single(saved.Buckets);
            Assert.Equal("Added", bucket.Name);
            Assert.Equal("New card", Assert.Single(bucket.Cards).Text);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData("", false)]
    [InlineData("html", false)]
    [InlineData("html", true)]
    public void CreationViewChoicesReplacePrioritiesUnlessReturnedToTheOriginal(string viewId, bool revert)
    {
        var window = CatalogEditorWindow();
        var source = new ZetlCreationTypeDocument { Id = "priority", Name = "Priority", TemplateId = "blank", ViewIds = ["markdown", "html"] };
        try
        {
            typeof(MainWindow).GetMethod("OpenCreationEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [source, false]);
            var choices = window.creationViewBox.ItemsSource!.Cast<ZetlViewDocument>().ToList();
            window.creationViewBox.SelectedItem = choices.Single(doc => doc.Id == viewId);
            if (revert) window.creationViewBox.SelectedItem = choices.Single(doc => doc.Id == "markdown");
            window.saveCreationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved = new ZetlCreationTypeStore(Path.Combine(defaultDraftDirectory, "creations")).LoadAll().Single(doc => doc.Id == source.Id);
            Assert.Equal(revert ? source.ViewIds : viewId.Length == 0 ? [] : new List<string> { viewId }, saved.ViewIds);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData("template", "typing")]
    [InlineData("template", "bucket")]
    [InlineData("template", "reopen")]
    [InlineData("template", "other-editor")]
    [InlineData("template", "retire")]
    [InlineData("creation", "typing")]
    [InlineData("creation", "view")]
    [InlineData("creation", "reopen")]
    [InlineData("creation", "other-editor")]
    [InlineData("creation", "retire")]
    public async Task CatalogDiscardCannotCloseANewerDraftOrSession(string kind, string interruption)
    {
        var window = CatalogEditorWindow();
        try
        {
            OpenCatalogEditor(window, kind);
            CatalogName(window, kind).Text = "First writing";
            // Deliberately do not dispatch TextChanged: capture must read current controls.
            var answer = new TaskCompletionSource<bool>();
            var cancelling = InvokeWorkflow(window, kind == "template" ? "CancelTemplateEditAsync" : "CancelCreationEditAsync",
                (Func<Task<bool>>)(() => answer.Task));
            Assert.False(cancelling.IsCompleted);
            if (interruption == "typing") CatalogName(window, kind).Text = "Later writing";
            else if (interruption == "bucket") window.templateBucketSeedsBox.Text = "Later card";
            else if (interruption == "view") window.creationViewBox.SelectedIndex = 0;
            else if (interruption == "reopen") OpenCatalogEditor(window, kind);
            else if (interruption == "other-editor") OpenCatalogEditor(window, kind == "template" ? "creation" : "template");
            else window.RetireLifetime();
            answer.SetResult(true);
            await cancelling;
            if (interruption is not "retire" and not "other-editor") Assert.True(CatalogView(window, kind).IsVisible);
            if (interruption == "typing") Assert.Equal("Later writing", CatalogName(window, kind).Text);
            if (interruption == "other-editor") Assert.True(CatalogView(window, kind == "template" ? "creation" : "template").IsVisible);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData("template", false)]
    [InlineData("creation", false)]
    [InlineData("template", true)]
    [InlineData("creation", true)]
    public async Task CatalogCancelClosesCleanOrConfirmedDrafts(string kind, bool dirty)
    {
        var window = CatalogEditorWindow();
        try
        {
            OpenCatalogEditor(window, kind);
            if (dirty) CatalogName(window, kind).Text = "Changed";
            var prompts = 0;
            await InvokeWorkflow(window, kind == "template" ? "CancelTemplateEditAsync" : "CancelCreationEditAsync",
                (Func<Task<bool>>)(() => { prompts++; return Task.FromResult(true); }));
            Assert.Equal(dirty ? 1 : 0, prompts);
            Assert.False(CatalogView(window, kind).IsVisible);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData("template")]
    [InlineData("creation")]
    public void CatalogValidationKeepsTheEditorOpenAndSuccessfulRetryWritesTheCapturedFields(string kind)
    {
        var window = CatalogEditorWindow();
        try
        {
            OpenCatalogEditor(window, kind);
            CatalogName(window, kind).Text = "";
            (kind == "template" ? window.saveTemplateButton : window.saveCreationButton).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(CatalogView(window, kind).IsVisible);
            Assert.True(kind == "template" ? window.templateErrorText.IsVisible : window.creationErrorText.IsVisible);
            CatalogName(window, kind).Text = "Saved without waiting";
            (kind == "template" ? window.saveTemplateButton : window.saveCreationButton).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(CatalogView(window, kind).IsVisible);
            var savedNames = kind == "template"
                ? new ZetlTemplateStore(Path.Combine(defaultDraftDirectory, "templates")).LoadAll().Select(doc => doc.Name)
                : new ZetlCreationTypeStore(Path.Combine(defaultDraftDirectory, "creations")).LoadAll().Select(doc => doc.Name);
            Assert.Contains("Saved without waiting", savedNames);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData("template")]
    [InlineData("creation")]
    public void CatalogWriteFailureKeepsTheOwnedDraftAndReusesItsIdOnRetry(string kind)
    {
        var blocked = Path.Combine(defaultDraftDirectory, "blocked");
        File.WriteAllText(blocked, "not a directory");
        var window = CatalogEditorWindow(kind == "template" ? blocked : null, kind == "creation" ? blocked : null);
        try
        {
            OpenCatalogEditor(window, kind);
            CatalogName(window, kind).Text = "Retained draft";
            (kind == "template" ? window.saveTemplateButton : window.saveCreationButton).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(CatalogView(window, kind).IsVisible);
            File.Delete(blocked);
            (kind == "template" ? window.saveTemplateButton : window.saveCreationButton).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(CatalogView(window, kind).IsVisible);
            Assert.Single(Directory.GetFiles(blocked, "*.json"));
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void TemplateRenamePreservesLegacySeedsMultilineCardsSettingsAndUnknownFields()
    {
        var window = CatalogEditorWindow();
        var source = KastnProjectCreationTests.Template();
        source.Buckets[0].Settings.DefaultKind = "Standard";
        source.Buckets[0].Settings.ReplayReviewBucketId = "review-id";
        source.Buckets[0].Cards[0].Text = "First line\nSecond line";
        source.ExtensionData = new() { ["future"] = JsonSerializer.SerializeToElement(new[] { "value" }) };
        try
        {
            typeof(MainWindow).GetMethod("OpenTemplateEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [source, false]);
            Dispatcher.UIThread.RunJobs();
            window.templateBucketNameBox.Text = "Renamed";
            window.saveTemplateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved = new ZetlTemplateStore(Path.Combine(defaultDraftDirectory, "templates")).LoadAll().Single(doc => doc.Id == source.Id);
            Assert.Equal("Renamed", saved.Buckets[0].Name);
            Assert.Equal(source.Buckets[0].Seeds, saved.Buckets[0].Seeds);
            Assert.Equal("First line\nSecond line", saved.Buckets[0].Cards[0].Text);
            Assert.Equal("Standard", saved.Buckets[0].Settings.DefaultKind);
            Assert.Equal("review-id", saved.Buckets[0].Settings.ReplayReviewBucketId);
            Assert.Equal("value", saved.ExtensionData!["future"][0].GetString());
            Assert.Equal("Fields", source.Buckets[0].Name);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public async Task RevertingBucketContentAndSettingsRestoresTheCleanBaseline()
    {
        var window = CatalogEditorWindow();
        try
        {
            OpenCatalogEditor(window, "template");
            var text = window.templateBucketSeedsBox.Text;
            window.templateBucketSeedsBox.Text = "Changed";
            window.templateBucketKindBox.SelectedItem = "Standard";
            Dispatcher.UIThread.RunJobs();
            window.templateBucketSeedsBox.Text = text;
            window.templateBucketKindBox.SelectedItem = "Replay";
            var prompts = 0;
            await InvokeWorkflow(window, "CancelTemplateEditAsync", (Func<Task<bool>>)(() => { prompts++; return Task.FromResult(false); }));
            Assert.Equal(0, prompts);
            Assert.False(window.templateEditorView.IsVisible);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void SelectingAnotherBucketCapturesBufferedEditsAndReorderKeepsThem()
    {
        var window = CatalogEditorWindow();
        try
        {
            OpenCatalogEditor(window, "template");
            window.templateBucketNameBox.Text = "Renamed";
            window.templateBucketSeedsBox.Text = "Card :: Body";
            window.templateBucketList.SelectedIndex = 1;
            window.templateBucketUpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.saveTemplateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved = new ZetlTemplateStore(Path.Combine(defaultDraftDirectory, "templates")).LoadAll().Single(doc => doc.Id == "test-template");
            Assert.Equal(new[] { "Notes", "Renamed" }, saved.Buckets.Select(bucket => bucket.Name));
            Assert.Equal("Card", Assert.Single(saved.Buckets[1].Cards).Title);
            Assert.Equal("Body", saved.Buckets[1].Cards[0].Text);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void CreationMetadataPreservesMissingReferencesAdditionalViewsAndSourceDocument()
    {
        var window = CatalogEditorWindow();
        var source = new ZetlCreationTypeDocument
        {
            Id = "missing-refs", Name = "Original", TemplateId = "missing-template", ViewIds = ["missing-view", "html"],
            ExtensionData = new() { ["future"] = JsonSerializer.SerializeToElement("kept") }
        };
        try
        {
            typeof(MainWindow).GetMethod("OpenCreationEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [source, false]);
            window.creationNameBox.Text = "Renamed";
            window.saveCreationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved = new ZetlCreationTypeStore(Path.Combine(defaultDraftDirectory, "creations")).LoadAll().Single(doc => doc.Id == source.Id);
            Assert.Equal("missing-template", saved.TemplateId);
            Assert.Equal(source.ViewIds, saved.ViewIds);
            Assert.Equal("kept", saved.ExtensionData!["future"].GetString());
            Assert.Equal("Original", source.Name);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData("template")]
    [InlineData("creation")]
    public void SwitchingCatalogEditorsAndSnapshotsKeepsOnlyTheActiveSession(string kind)
    {
        var window = CatalogEditorWindow();
        try
        {
            OpenCatalogEditor(window, kind == "template" ? "creation" : "template");
            OpenCatalogEditor(window, kind);
            typeof(MainWindow).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [new KastnSessionSnapshot(KastnConnectionState.Online, "Connected", [], null)]);
            Assert.True(CatalogView(window, kind).IsVisible);
            Assert.False(CatalogView(window, kind == "template" ? "creation" : "template").IsVisible);
            OpenViewForUi(window, new() { Id = "view-draft", Name = "View", Kind = ZetlViewKinds.Html });
            typeof(MainWindow).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [new KastnSessionSnapshot(KastnConnectionState.Online, "Connected", [], null)]);
            Assert.True(window.viewEditorView.IsVisible);
            Assert.False(CatalogView(window, kind).IsVisible);
        }
        finally { CloseWindow(window); }
    }
}
