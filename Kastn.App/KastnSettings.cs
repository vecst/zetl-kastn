using ZETL;

namespace KASTN;

// One UI-thread owner per app session. The resolved path stays fixed, including
// for test overrides. Callers treat returned snapshots as read-only; refreshing
// replaces them so an operation can keep its original preferences across awaits.
internal sealed class KastnSettings
{
    private readonly TimeProvider time;
    private ZetlAppSettingsStore store;
    private long loadedAt;
    private int loadedStamp;

    public KastnSettings(string? path = null, TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        loadedStamp = ZetlAppSettingsStore.SaveStamp;
        store = new(path, Console.Error.WriteLine);
        loadedAt = this.time.GetTimestamp();
    }

    public string SettingsPath => store.SettingsPath;

    public ZetlAppSettings Current
    {
        get
        {
            if (loadedStamp != ZetlAppSettingsStore.SaveStamp
                || time.GetElapsedTime(loadedAt) >= TimeSpan.FromSeconds(1))
            {
                Refresh();
            }
            return store.Settings;
        }
    }

    public ZetlAppSettings Refresh()
    {
        // Capture before loading: a save racing the read must invalidate it on
        // the next access, rather than stamp stale data with the newer version.
        var stamp = ZetlAppSettingsStore.SaveStamp;
        var refreshed = new ZetlAppSettingsStore(SettingsPath, Console.Error.WriteLine);
        store = refreshed;
        loadedStamp = stamp;
        loadedAt = time.GetTimestamp();
        return store.Settings;
    }

    public void RememberTemporaryTemplateLane(string lane)
    {
        var normalized = ZetlKastnTemplateLaneDefault.Normalize(lane);
        if (normalized.Length == 0)
            throw new ArgumentException("A temporary lane must be Normal or Shift.", nameof(lane));

        // Reload at the write, not at the start of the lane-picker dialog. This
        // preserves preferences edited by Zetl while the dialog was open.
        store.Update(settings => settings.KastnTemporaryTemplateLaneDefault = normalized);
        loadedAt = time.GetTimestamp();
        // Leave the old stamp so the next read also detects a concurrent save.
    }
}
