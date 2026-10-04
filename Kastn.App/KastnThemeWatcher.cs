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
    private readonly KastnSettings settings;
    private readonly FileSystemWatcher? watcher;
    private readonly DispatcherTimer debounce;
    private volatile bool disposed;

    public KastnThemeWatcher(ZetlThemeManager themeManager, KastnSettings settings)
    {
        this.themeManager = themeManager;
        this.settings = settings;
        var dataDir = Path.GetDirectoryName(settings.SettingsPath);

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
            watcher.Deleted += OnChanged;
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
            e.FullPath, settings.SettingsPath, StringComparison.OrdinalIgnoreCase)
            || e is RenamedEventArgs renamed && string.Equals(
                renamed.OldFullPath, settings.SettingsPath, StringComparison.OrdinalIgnoreCase);
        var relative = (e.Name ?? "").Replace('\\', '/');
        var isTheme = relative.StartsWith("themes/", StringComparison.OrdinalIgnoreCase)
            && e.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        if (!isSettings && !isTheme)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (disposed) return;
            debounce.Stop();
            debounce.Start();
        });
    }

    private void Reload()
    {
        if (disposed) return;
        try
        {
            var current = settings.Refresh();
            var theme = new ZetlThemeStore().Resolve(current.ThemeId);
            themeManager.ApplyIfChanged(theme, current.ThemeVariant);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A transient read race (Zetl mid-write) — the next event reloads.
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        debounce.Stop();
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }
}
