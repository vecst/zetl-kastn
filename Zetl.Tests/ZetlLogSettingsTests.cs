using Xunit;

namespace ZETL.Tests;

public class ZetlLogSettingsTests
{
    [Fact]
    public void LogSettingsTakeEffectWithinTheirRanges()
    {
        var settings = new ZetlAppSettings { LogRetentionDays = 30, LogMaxNotesPerDay = 500, LogFlushIntervalMs = 2000 };
        Assert.Equal((30, 500, 2000),
            (settings.EffectiveLogRetentionDays, settings.EffectiveLogMaxNotesPerDay, settings.EffectiveLogFlushIntervalMs));

        // A hand-edited file cannot switch logging off or trim every line.
        var edited = new ZetlAppSettings { LogRetentionDays = 0, LogMaxNotesPerDay = 0, LogFlushIntervalMs = 1 };
        Assert.Equal((1, 100, 500),
            (edited.EffectiveLogRetentionDays, edited.EffectiveLogMaxNotesPerDay, edited.EffectiveLogFlushIntervalMs));
    }

    [Fact]
    public void RetentionSettingsBoundTheLogProject()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ZetlLogSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "log-settings");
            var settings = new ZetlAppSettings { LogMaxNotesPerDay = 100 };
            store.AppendLogSlips(Enumerable.Range(1, 150).Select(i => $"line {i}").ToList(),
                settings.EffectiveLogRetentionDays, settings.EffectiveLogMaxNotesPerDay);

            var day = store.State.Projects.Single(project => project.Name == "Zetl Logs")
                .Buckets.Single(bucket => bucket.Slips.Count > 0);
            Assert.Equal(100, day.Slips.Count);
            Assert.Equal("line 150", day.Slips[^1].Text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
