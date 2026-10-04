using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaFact]
    public void WindowUsesInjectedSettingsForLaneLabelsAndUntitledSlips()
    {
        var path = Path.Combine(defaultDraftDirectory, "settings.json");
        JsonFile.WriteAtomic(path, new ZetlAppSettings { KastnMainLaneLabel = "Work", UntitledSlipTitle = "New note" });
        var owner = new KastnSettings(path, new SettingsTestTime());
        var window = new MainWindow(new KastnConnectionController(_ => Task.CompletedTask), settings: owner);
        try
        {
            var lane = typeof(MainWindow).GetMethod("LaneLabel", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var untitled = typeof(MainWindow).GetMethod("IsUntitledKastnSlip", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var slip = new ZetlSlipSnapshot
            {
                Id = "new", Revision = 1, Type = ZetlSlipType.Text, BucketId = "bucket",
                CapturedAtUtc = DateTimeOffset.UnixEpoch, Title = "New note", Source = "kastn", Text = ""
            };
            Assert.Equal("Work", lane.Invoke(window, ["Normal"]));
            Assert.Equal(true, untitled.Invoke(window, [slip]));
            JsonFile.WriteAtomic(path, new ZetlAppSettings { KastnMainLaneLabel = "Updated", UntitledSlipTitle = "Fresh note" });
            owner.Refresh();
            Assert.Equal("Updated", lane.Invoke(window, ["Normal"]));
            Assert.Equal(false, untitled.Invoke(window, [slip]));
            Assert.Equal(true, untitled.Invoke(window, [slip with { Title = "Fresh note" }]));
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void ThemeReloadRefreshesTheSameOwnerEvenAfterDefaultPathChanges()
    {
        var path = Path.Combine(defaultDraftDirectory, "settings.json");
        var priorOverride = ZetlAppSettingsStore.DefaultSettingsPathOverride;
        var owner = new KastnSettings(path, new SettingsTestTime());
        using var watcher = new KastnThemeWatcher(new ZetlThemeManager(Avalonia.Application.Current!), owner);
        try
        {
            ZetlAppSettingsStore.DefaultSettingsPathOverride = Path.Combine(defaultDraftDirectory, "other.json");
            JsonFile.WriteAtomic(path, new ZetlAppSettings { KastnAutosave = false });
            typeof(KastnThemeWatcher).GetMethod("Reload", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(watcher, []);
            Assert.False(owner.Current.KastnAutosave);
            Assert.False(File.Exists(ZetlAppSettingsStore.DefaultSettingsPathOverride));
        }
        finally { ZetlAppSettingsStore.DefaultSettingsPathOverride = priorOverride; }
    }

    [AvaloniaFact]
    public void WindowCloseReadsChangedPreferencesFromItsOwner()
    {
        var path = Path.Combine(defaultDraftDirectory, "settings.json");
        JsonFile.WriteAtomic(path, new ZetlAppSettings { KastnCloseToTray = true });
        var owner = new KastnSettings(path, new SettingsTestTime());
        var window = new MainWindow(new KastnConnectionController(_ => Task.CompletedTask), settings: owner);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.False(closed);
            Assert.False(window.IsVisible);
            JsonFile.WriteAtomic(path, new ZetlAppSettings { KastnCloseToTray = false });
            owner.Refresh();
            window.Show();
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(closed);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData("change")]
    [InlineData("delete")]
    [InlineData("rename-away")]
    [InlineData("theme")]
    public void ThemeWatcherQueuesRelevantFileEvents(string change)
    {
        var path = Path.Combine(defaultDraftDirectory, "settings.json");
        using var watcher = new KastnThemeWatcher(new ZetlThemeManager(Avalonia.Application.Current!), new KastnSettings(path));
        FileSystemEventArgs notification = change switch
        {
            "delete" => new FileSystemEventArgs(WatcherChangeTypes.Deleted, defaultDraftDirectory, "settings.json"),
            "rename-away" => new RenamedEventArgs(WatcherChangeTypes.Renamed, defaultDraftDirectory, "old.json", "settings.json"),
            "theme" => new FileSystemEventArgs(WatcherChangeTypes.Changed, defaultDraftDirectory, "themes/custom.json"),
            _ => new FileSystemEventArgs(WatcherChangeTypes.Changed, defaultDraftDirectory, "settings.json")
        };
        typeof(KastnThemeWatcher).GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(watcher, [this, notification]);
        Dispatcher.UIThread.RunJobs();
        var timer = (DispatcherTimer)typeof(KastnThemeWatcher).GetField("debounce", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(watcher)!;
        Assert.True(timer.IsEnabled);
    }

    [AvaloniaFact]
    public void ThemeWatcherIgnoresAnUnrelatedFileWithTheSameName()
    {
        var path = Path.Combine(defaultDraftDirectory, "settings.json");
        using var watcher = new KastnThemeWatcher(new ZetlThemeManager(Avalonia.Application.Current!), new KastnSettings(path));
        typeof(KastnThemeWatcher).GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(watcher, [this, new FileSystemEventArgs(WatcherChangeTypes.Changed,
                Path.Combine(defaultDraftDirectory, "unrelated"), "settings.json")]);
        Dispatcher.UIThread.RunJobs();
        var timer = (DispatcherTimer)typeof(KastnThemeWatcher).GetField("debounce", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(watcher)!;
        Assert.False(timer.IsEnabled);
    }
}
