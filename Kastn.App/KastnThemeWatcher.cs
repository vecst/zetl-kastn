using System;
using System.IO;
using Avalonia.Threading;
using ZETL;

namespace KASTN;

// Watches the shared Zetl app-settings file and themes/ folder so a theme change
// made in Zetl (which owns the single theme editor) re-applies live to an open Kastn
// window without a restart. settings.json is the shared source of truth for the
// selected theme id/variant; themes/<id>.json carries an edited theme's palette.
internal sealed class KastnThemeWatcher : IDisposable
{
    private readonly ZetlThemeManager themeManager;
    private readonly string settingsFileName;
    private readonly FileSystemWatcher? watcher;
    private readonly DispatcherTimer debounce;

    public KastnThemeWatcher(ZetlThemeManager themeManager)
    {
        this.themeManager = themeManager;
        var settingsStore = new ZetlAppSettingsStore();
        settingsFileName = Path.GetFileName(settingsStore.SettingsPath);
        var dataDir = Path.GetDirectoryName(settingsStore.SettingsPath);

        // Coalesce the burst of file events from an atomic write into one reload.
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            Reload();
        };

        if (string.IsNullOrEmpty(dataDir))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(dataDir);
            watcher = new FileSystemWatcher(dataDir)
            {
                IncludeSubdirectories = true, // also catch themes/<id>.json
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Renamed += OnChanged;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Without a watcher Kastn still picks up the theme on its next launch.
            watcher = null;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var isSettings = string.Equals(
            Path.GetFileName(e.FullPath), settingsFileName, StringComparison.OrdinalIgnoreCase);
        var relative = (e.Name ?? "").Replace('\\', '/');
        var isTheme = relative.StartsWith("themes/", StringComparison.OrdinalIgnoreCase)
            && e.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        if (!isSettings && !isTheme)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            debounce.Stop();
            debounce.Start();
        });
    }

    private void Reload()
    {
        try
        {
            var settings = new ZetlAppSettingsStore().Settings;
            var theme = new ZetlThemeStore().Resolve(settings.ThemeId);
            themeManager.ApplyIfChanged(theme, settings.ThemeVariant);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A transient read race (Zetl mid-write) — the next event reloads.
        }
    }

    public void Dispose()
    {
        debounce.Stop();
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }
}
