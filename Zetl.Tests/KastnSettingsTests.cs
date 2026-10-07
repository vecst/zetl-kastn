using System.Text.Json;
using KASTN;
using Xunit;

namespace ZETL.Tests;

[Collection(RealTimeCollection.Name)]
public sealed class KastnSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "KastnSettings", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(directory, "settings.json");

    [Fact]
    public void HotReadsReuseSnapshotAndExternalChangesRefreshAfterOneSecond()
    {
        JsonFile.WriteAtomic(SettingsPath, new ZetlAppSettings { UntitledSlipTitle = "Original" });
        var time = new SettingsTestTime();
        var owner = new KastnSettings(SettingsPath, time);
        var original = owner.Current;
        // Atomic writes from the other process do not increment this process's stamp.
        JsonFile.WriteAtomic(SettingsPath, new ZetlAppSettings { UntitledSlipTitle = "External" });
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Same(original, owner.Current);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("External", owner.Current.UntitledSlipTitle);
        Assert.NotSame(original, owner.Current);
        Assert.Equal("Original", original.UntitledSlipTitle);
    }

    [Fact]
    public void LocalSaveInvalidatesImmediatelyWithoutWaitingForExpiry()
    {
        var owner = new KastnSettings(SettingsPath, new SettingsTestTime());
        var original = owner.Current;
        var otherStore = new ZetlAppSettingsStore(SettingsPath);
        otherStore.Settings.KastnAutosave = false;
        otherStore.Save();
        Assert.False(owner.Current.KastnAutosave);
        Assert.NotSame(original, owner.Current);
    }

    [Fact]
    public void ExplicitRefreshUpdatesTheSharedSnapshotBeforeExpiry()
    {
        var owner = new KastnSettings(SettingsPath, new SettingsTestTime());
        JsonFile.WriteAtomic(SettingsPath, new ZetlAppSettings { KastnCloseToTray = false });
        var refreshed = owner.Refresh();
        Assert.False(refreshed.KastnCloseToTray);
        Assert.Same(refreshed, owner.Current);
    }

    [Fact]
    public void OwnersKeepTheirResolvedPathWhenTheDefaultOverrideChanges()
    {
        var prior = ZetlAppSettingsStore.DefaultSettingsPathOverride;
        var secondPath = Path.Combine(directory, "other.json");
        try
        {
            JsonFile.WriteAtomic(SettingsPath, new ZetlAppSettings { UntitledSlipTitle = "First" });
            JsonFile.WriteAtomic(secondPath, new ZetlAppSettings { UntitledSlipTitle = "Second" });
            ZetlAppSettingsStore.DefaultSettingsPathOverride = SettingsPath;
            var first = new KastnSettings();
            ZetlAppSettingsStore.DefaultSettingsPathOverride = secondPath;
            var second = new KastnSettings();
            first.RememberTemporaryTemplateLane("Shift");
            Assert.Equal("First", first.Refresh().UntitledSlipTitle);
            Assert.Equal("Second", second.Refresh().UntitledSlipTitle);
            Assert.Equal("Shift", new ZetlAppSettingsStore(SettingsPath).Settings.KastnTemporaryTemplateLaneDefault);
            Assert.Equal("", new ZetlAppSettingsStore(secondPath).Settings.KastnTemporaryTemplateLaneDefault);
        }
        finally { ZetlAppSettingsStore.DefaultSettingsPathOverride = prior; }
    }

    [Fact]
    public void RelativePathsAreResolvedOnce()
    {
        var owner = new KastnSettings(Path.GetRelativePath(Environment.CurrentDirectory, SettingsPath));
        Assert.Equal(SettingsPath, owner.SettingsPath);
        owner.RememberTemporaryTemplateLane("Normal");
        Assert.Equal("Normal", new ZetlAppSettingsStore(SettingsPath).Settings.KastnTemporaryTemplateLaneDefault);
    }

    [Fact]
    public void RememberLaneMergesLatestPreferencesAndPreservesCapturedSnapshot()
    {
        var owner = new KastnSettings(SettingsPath, new SettingsTestTime());
        var captured = owner.Current;
        JsonFile.WriteAtomic(SettingsPath, new ZetlAppSettings
        {
            KastnAutosave = false, KastnMainLaneLabel = "Work", ThemeVariant = "Dark",
            PdfPageFormat = "A4", PdfFontSize = 17, ToastDisplayMs = 1234
        });
        owner.RememberTemporaryTemplateLane("shift");
        var saved = new ZetlAppSettingsStore(SettingsPath).Settings;
        Assert.Equal("Shift", saved.KastnTemporaryTemplateLaneDefault);
        Assert.False(saved.KastnAutosave);
        Assert.Equal("Work", saved.KastnMainLaneLabel);
        Assert.Equal("Dark", saved.ThemeVariant);
        Assert.Equal("A4", saved.PdfPageFormat);
        Assert.Equal(17, saved.PdfFontSize);
        Assert.Equal(1234, saved.ToastDisplayMs);
        Assert.Equal("Shift", owner.Current.KastnTemporaryTemplateLaneDefault);
        Assert.Equal("", captured.KastnTemporaryTemplateLaneDefault);
        Assert.True(captured.KastnAutosave);
    }

    [Fact]
    public void RememberLaneInvalidatesOtherOwnersImmediately()
    {
        var first = new KastnSettings(SettingsPath, new SettingsTestTime());
        var second = new KastnSettings(SettingsPath, new SettingsTestTime());
        first.RememberTemporaryTemplateLane("Normal");
        Assert.Equal("Normal", second.Current.KastnTemporaryTemplateLaneDefault);
    }

    [Fact]
    public void InvalidLaneDoesNotWriteOrInvalidateTheCache()
    {
        var owner = new KastnSettings(SettingsPath);
        var original = owner.Current;
        var stamp = ZetlAppSettingsStore.SaveStamp;
        Assert.Throws<ArgumentException>(() => owner.RememberTemporaryTemplateLane("Elsewhere"));
        Assert.False(File.Exists(SettingsPath));
        Assert.Equal(stamp, ZetlAppSettingsStore.SaveStamp);
        Assert.Same(original, owner.Current);
    }

    [Fact]
    public void CorruptionDuringDialogCannotBeOverwrittenByRememberingLane()
    {
        JsonFile.WriteAtomic(SettingsPath, new ZetlAppSettings());
        var owner = new KastnSettings(SettingsPath, new SettingsTestTime());
        var original = owner.Current;
        File.WriteAllText(SettingsPath, "{corrupt");
        var stamp = ZetlAppSettingsStore.SaveStamp;
        Assert.Throws<JsonException>(() => owner.RememberTemporaryTemplateLane("Shift"));
        Assert.Equal("{corrupt", File.ReadAllText(SettingsPath));
        Assert.Empty(Directory.GetFiles(directory, "*.corrupt-*"));
        Assert.Equal(stamp, ZetlAppSettingsStore.SaveStamp);
        Assert.Same(original, owner.Current);
    }

    [Fact]
    public void FailedReadPreservesTheFileAndCachedPreferences() =>
        FailedReadOrWritePreservesTheFileAndCachedPreferences(FileShare.None);

    // A reader that allows other readers only blocks the replacing write on Windows.
    [WindowsOnlyFact]
    public void FailedWritePreservesTheFileAndCachedPreferences() =>
        FailedReadOrWritePreservesTheFileAndCachedPreferences(FileShare.Read);

    private void FailedReadOrWritePreservesTheFileAndCachedPreferences(FileShare sharing)
    {
        JsonFile.WriteAtomic(SettingsPath, new ZetlAppSettings { ThemeVariant = "Dark" });
        var originalBytes = File.ReadAllBytes(SettingsPath);
        var owner = new KastnSettings(SettingsPath, new SettingsTestTime());
        var original = owner.Current;
        var stamp = ZetlAppSettingsStore.SaveStamp;
        using (File.Open(SettingsPath, FileMode.Open, FileAccess.Read, sharing))
            Assert.ThrowsAny<IOException>(() => owner.RememberTemporaryTemplateLane("Shift"));
        Assert.Equal(originalBytes, File.ReadAllBytes(SettingsPath));
        Assert.Equal(stamp, ZetlAppSettingsStore.SaveStamp);
        Assert.Same(original, owner.Current);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        owner.RememberTemporaryTemplateLane("Shift");
        Assert.Equal("Shift", owner.Current.KastnTemporaryTemplateLaneDefault);
        Assert.Equal("Dark", owner.Current.ThemeVariant);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

internal sealed class SettingsTestTime : TimeProvider
{
    private long timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => timestamp;
    public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
}
