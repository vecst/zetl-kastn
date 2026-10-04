using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaTheory]
    [InlineData("keep", true, true)]
    [InlineData("keep", false, false)]
    [InlineData("discard", true, true)]
    [InlineData("discard", false, true)]
    [InlineData("cancel", true, false)]
    [InlineData("cancel", false, false)]
    public async Task UnsavedCloseRequiresDurableRecoveryOrExplicitDiscard(
        string choice, bool journalWorks, bool expected)
    {
        var action = choice switch
        {
            "keep" => KastnDialogs.UnsavedCloseAction.KeepRecovery,
            "discard" => KastnDialogs.UnsavedCloseAction.Discard,
            _ => KastnDialogs.UnsavedCloseAction.Cancel
        };
        if (!journalWorks) KastnDraftStore.DefaultPathOverride = defaultDraftDirectory;
        var (window, controller, project) = WorkflowWindow();
        try
        {
            window.slipEditor.Text = "local writing";
            Dispatcher.UIThread.RunJobs();
            InterruptLinkWorkflow(window, controller, project, "offline");
            var draft = WindowField<KastnDraftStore>(window, "draftStore");
            var accepted = await PrepareExitForTest(window, (stored, reason) =>
            {
                Assert.Equal(journalWorks, stored);
                Assert.Contains("offline", reason);
                return Task.FromResult(action);
            });
            Assert.Equal(expected, accepted);
            if (action == KastnDialogs.UnsavedCloseAction.Discard) Assert.Null(draft.Draft);
            else Assert.Equal("local writing", draft.Draft?.DraftText);
            Assert.True(window.IsVisible);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData("typing")]
    [InlineData("styles")]
    [InlineData("pending-style")]
    [InlineData("reselection")]
    [InlineData("project")]
    [InlineData("remote")]
    [InlineData("close")]
    public async Task OldDiscardAnswerCannotDiscardAChangedEditor(string interruption)
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            window.slipEditor.Text = "original local draft";
            Dispatcher.UIThread.RunJobs();
            var answer = new TaskCompletionSource<KastnDialogs.UnsavedCloseAction>();
            var preparing = PrepareExitForTest(window, (_, _) => answer.Task);
            Assert.False(preparing.IsCompleted);
            if (interruption == "close") CloseWindow(window);
            else if (interruption == "reselection")
            {
                // Ordinary tree navigation refuses this unsaved offline draft;
                // model a replaced editor session returning to the same slip.
                window.editorState.Select(project.Slips[1]);
                window.editorState.Select(project.Slips[0]);
            }
            else InterruptLinkWorkflow(window, controller, project, interruption);
            answer.SetResult(KastnDialogs.UnsavedCloseAction.Discard);
            Assert.False(await preparing);
            Assert.NotNull(WindowField<KastnDraftStore>(window, "draftStore").Draft);
            if (interruption is "typing" or "styles" or "pending-style" or "remote")
                Assert.Contains("closing was cancelled", window.statusText.Text);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public async Task UnchangedConflictCanStillBeExplicitlyDiscarded()
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            window.slipEditor.Text = "local writing";
            Dispatcher.UIThread.RunJobs();
            InterruptLinkWorkflow(window, controller, project, "remote");
            Assert.NotNull(window.editorState.ConflictCurrent);
            Assert.True(await PrepareExitForTest(window, (_, reason) =>
            {
                Assert.Contains("conflict", reason);
                return Task.FromResult(KastnDialogs.UnsavedCloseAction.Discard);
            }));
            Assert.Null(WindowField<KastnDraftStore>(window, "draftStore").Draft);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public async Task ClosedWindowStopsTimersRejectsQueuedSnapshotsAndCannotReactivate()
    {
        var (window, controller, project) = WorkflowWindow();
        window.slipEditor.Text = "draft";
        Dispatcher.UIThread.RunJobs();
        typeof(MainWindow).GetProperty("saving", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(window, true);
        var timers = new[] { "draftJournalTimer", "savingVisualTimer", "dragScrollTimer", "boardDragScrollTimer" }
            .Select(name => WindowField<DispatcherTimer>(window, name)).ToArray();
        foreach (var timer in timers) timer.Start();
        var callback = (EventHandler<KastnSessionSnapshot>)typeof(KastnConnectionController)
            .GetField("SnapshotChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controller)!;
        callback(controller, new(KastnConnectionState.Online, "later", [], project with { Id = "later-project" }));
        var history = WindowField<KastnEditHistory>(window, "editHistory");
        var generation = history.Generation;
        WindowField<KastnWindowLifetime>(window, "lifetime").CloseConfirmed();
        Dispatcher.UIThread.RunJobs();
        Assert.All(timers, timer => Assert.False(timer.IsEnabled));
        Assert.True(history.Generation > generation);
        Assert.Equal(project.Id, WindowField<ZetlProjectSnapshot>(window, "currentProject").Id);
        Assert.Empty(WindowField<KastnReaderPresenter>(window, "readerPresenter").Blocks);
        Assert.True(WindowField<KastnBoardPresenter>(window, "boardPresenter").IsDisposed);
        Assert.Null(typeof(KastnConnectionController).GetField("SnapshotChanged",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controller));
        var text = window.statusText.Text;
        await window.ActivateRequestAsync("other");
        window.ReportActivationFailure(new IOException("late failure"));
        Assert.False(await window.RequestShutdownDecisionAsync());
        Assert.False(window.IsVisible);
        Assert.Equal(text, window.statusText.Text);
    }

    [AvaloniaTheory]
    [InlineData("unchanged")]
    [InlineData("typing")]
    [InlineData("project")]
    [InlineData("close")]
    public async Task ExitPreparationJoinsAutosaveAndProtectsItsEditorSession(string interruption)
    {
        var directory = Path.Combine(Path.GetTempPath(), "KastnCloseSaveUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        using var release = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource();
        var count = 0;
        try
        {
            var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "kastn-ui");
            var project = store.CreateProject("Closing", ["Inbox"], "Inbox");
            var slip = store.AddSlip(project.Buckets[0], "baseline", "copy");
            var pipe = $"close-save-{Guid.NewGuid():N}";
            using var server = new ZetlIpcServer(new ZetlProjectService(store), pipe, log: null,
                dropResponseForTesting: command =>
                {
                    if (command.Kind == ZetlCommandKind.UpdateSlip)
                    {
                        Interlocked.Increment(ref count);
                        arrived.TrySetResult();
                        release.Wait(TimeSpan.FromSeconds(5));
                    }
                    return false;
                });
            server.Start();
            await using var controller = new KastnConnectionController(_ => Task.CompletedTask, pipe,
                TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(25));
            controller.Start(project.Id);
            await WaitForConditionAsync(() => controller.Current.Project?.Id == project.Id, "Project should load.");
            var drafts = new KastnDraftStore(Path.Combine(directory, "draft.json"));
            window = new MainWindow(controller, drafts);
            window.Show();
            window.slipEditor.Text = "submitted draft";
            Dispatcher.UIThread.RunJobs();
            var autosave = InvokeWorkflow(window, "SaveEditorAsync");
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var preparing = PrepareExitForTest(window, (_, _) =>
                Task.FromResult(KastnDialogs.UnsavedCloseAction.Cancel));
            if (interruption == "typing") window.slipEditor.Text = "newer writing";
            if (interruption == "project")
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(project) with { Id = "other-project" });
            if (interruption == "close") CloseWindow(window);
            Dispatcher.UIThread.RunJobs();
            release.Set();
            Assert.Equal(interruption == "unchanged", await preparing.WaitAsync(TimeSpan.FromSeconds(5)));
            await autosave;
            Assert.Equal(1, count);
            Assert.Equal("submitted draft", slip.Text);
            if (interruption == "typing")
            {
                Assert.Equal("newer writing", window.slipEditor.Text);
                Assert.Equal("newer writing", drafts.Draft?.DraftText);
                Assert.True(window.editorState.IsDirty);
            }
        }
        finally
        {
            release.Set();
            if (window is not null) CloseWindow(window);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task<bool> PrepareExitForTest(MainWindow window,
        Func<bool, string, Task<KastnDialogs.UnsavedCloseAction>> decide) =>
        (Task<bool>)InvokeWorkflow(window, "PrepareEditorForExitAsync", decide);

    [AvaloniaFact]
    public void DisposedThemeWatcherCannotRestartFromQueuedFileEvents()
    {
        var settingsPath = ZetlAppSettingsStore.DefaultSettingsPathOverride;
        KastnThemeWatcher? watcher = null;
        try
        {
            ZetlAppSettingsStore.DefaultSettingsPathOverride = Path.Combine(defaultDraftDirectory, "settings.json");
            watcher = new(new ZetlThemeManager(Avalonia.Application.Current!));
            typeof(KastnThemeWatcher).GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(watcher, [this, new FileSystemEventArgs(WatcherChangeTypes.Changed, defaultDraftDirectory, "settings.json")]);
            watcher.Dispose();
            Dispatcher.UIThread.RunJobs();
            var timer = (DispatcherTimer)typeof(KastnThemeWatcher)
                .GetField("debounce", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(watcher)!;
            Assert.False(timer.IsEnabled);
            watcher.Dispose();
        }
        finally
        {
            watcher?.Dispose();
            ZetlAppSettingsStore.DefaultSettingsPathOverride = settingsPath;
        }
    }
}
