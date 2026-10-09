using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace ZETL;

/// <summary>
/// Starts Zetl when the user logs in. The login entry itself is the only record:
/// Windows' per-user Run key, or the XDG autostart file on Linux (the same file
/// install.sh --autostart writes). Nothing is kept in settings.json, so what
/// Settings shows always matches what the desktop will actually do, including
/// after Task Manager or a desktop's autostart settings turn it off.
/// </summary>
internal sealed class ZetlStartAtLogin
{
    private readonly IZetlLoginEntry? entry;
    private readonly string? command;

    internal ZetlStartAtLogin(IZetlLoginEntry? entry, string? command)
    {
        this.entry = entry;
        this.command = command;
    }

    public static ZetlStartAtLogin ForThisProcess()
    {
        var command = LaunchCommand(
            ZetlAppImage.CurrentPath(),
            Environment.ProcessPath,
            OperatingSystem.IsWindows());
        IZetlLoginEntry? entry = OperatingSystem.IsWindows()
            ? new ZetlWindowsRunEntry()
            : OperatingSystem.IsLinux()
                ? new ZetlXdgAutostartEntry(ZetlXdgAutostartEntry.DefaultPath())
                : null;
        return new ZetlStartAtLogin(entry, command);
    }

    /// <summary>
    /// What a login should run: the AppImage when Zetl runs from one (its mount
    /// is gone at the next login), otherwise this executable. Null when Zetl runs
    /// through `dotnet Zetl.dll`, which has nothing stable to start.
    /// </summary>
    internal static string? LaunchCommand(string? appImage, string? processPath, bool windows)
    {
        if (!string.IsNullOrEmpty(appImage))
        {
            return appImage;
        }

        if (processPath is null)
        {
            return null;
        }

        // Split by the target platform's separators, not the host's, so this
        // answers the same on every OS (Windows paths use either slash).
        var fileName = windows
            ? processPath[(processPath.LastIndexOfAny(['\\', '/']) + 1)..]
            : processPath[(processPath.LastIndexOf('/') + 1)..];
        return windows
            ? string.Equals(fileName, "Zetl.exe", StringComparison.OrdinalIgnoreCase) ? processPath : null
            : string.Equals(fileName, "Zetl", StringComparison.Ordinal) ? processPath : null;
    }

    public bool IsAvailable => entry is not null && command is not null;

    public bool IsEnabled => entry?.IsEnabled() == true;

    public void Set(bool enabled)
    {
        if (entry is null)
        {
            return;
        }

        if (!enabled)
        {
            entry.Remove();
            return;
        }

        if (command is null)
        {
            throw new InvalidOperationException("This copy of Zetl has no fixed program file to start at login.");
        }

        entry.Write(command);
    }

    /// <summary>
    /// Points an enabled entry at this copy when the program it names is gone
    /// (the folder moved or an old AppImage was deleted). An entry naming another
    /// copy that still exists is left alone, so running a test build never takes
    /// over login. Returns true when the entry was rewritten.
    /// </summary>
    public bool RepairIfMoved()
    {
        if (entry is null || command is null || !entry.IsEnabled()
            || entry.RegisteredProgram() is not { } registered
            || File.Exists(registered))
        {
            return false;
        }

        entry.Write(command);
        return true;
    }
}

internal interface IZetlLoginEntry
{
    bool IsEnabled();

    /// <summary>The program the entry starts, or null when there is no entry.</summary>
    string? RegisteredProgram();

    void Write(string program);

    void Remove();
}

/// <summary>
/// HKCU\...\CurrentVersion\Run, plus Explorer's StartupApproved record, which
/// Task Manager's Startup apps page writes to switch an entry off without
/// deleting it (an odd first byte means disabled).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ZetlWindowsRunEntry(
    string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run",
    string approvedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
    string valueName = "Zetl") : IZetlLoginEntry
{
    public bool IsEnabled()
    {
        if (RegisteredProgram() is null)
        {
            return false;
        }

        using var approved = Registry.CurrentUser.OpenSubKey(approvedKey);
        return approved?.GetValue(valueName) is not byte[] { Length: > 0 } state || state[0] % 2 == 0;
    }

    public string? RegisteredProgram()
    {
        using var run = Registry.CurrentUser.OpenSubKey(runKey);
        return run?.GetValue(valueName) is string value && value.Trim().Length > 0
            ? UnquoteProgram(value)
            : null;
    }

    public void Write(string program)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(runKey))
        {
            run.SetValue(valueName, $"\"{program}\"", RegistryValueKind.String);
        }

        // Turning it on from Zetl also clears a "disabled" left by Task Manager.
        using var approved = Registry.CurrentUser.OpenSubKey(approvedKey, writable: true);
        approved?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public void Remove()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(runKey, writable: true))
        {
            run?.DeleteValue(valueName, throwOnMissingValue: false);
        }

        using var approved = Registry.CurrentUser.OpenSubKey(approvedKey, writable: true);
        approved?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private static string UnquoteProgram(string value)
    {
        value = value.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 0 ? value[1..end] : value.Trim('"');
        }

        var space = value.IndexOf(' ');
        return space > 0 ? value[..space] : value;
    }
}

/// <summary>
/// $XDG_CONFIG_HOME/autostart/zetl.desktop. A desktop's autostart settings may
/// disable the entry with Hidden=true or X-GNOME-Autostart-enabled=false
/// rather than delete it; both count as off.
/// </summary>
internal sealed class ZetlXdgAutostartEntry(string path) : IZetlLoginEntry
{
    public static string DefaultPath()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(configHome))
        {
            configHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(configHome, "autostart", "zetl.desktop");
    }

    public bool IsEnabled()
    {
        var keys = ReadKeys();
        return keys is not null
            && keys.ContainsKey("Exec")
            && !IsTrue(keys.GetValueOrDefault("Hidden"))
            && !string.Equals(keys.GetValueOrDefault("X-GNOME-Autostart-enabled"), "false", StringComparison.OrdinalIgnoreCase);
    }

    public string? RegisteredProgram() =>
        ReadKeys()?.GetValueOrDefault("Exec") is { } exec ? FirstExecArgument(exec) : null;

    public void Write(string program)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = new StringBuilder()
            .Append("[Desktop Entry]\n")
            .Append("Type=Application\n")
            .Append("Name=Zetl\n")
            .Append("GenericName=Capture shortcuts\n")
            .Append("Comment=Tap a shortcut as always; hold it to capture into Zetl\n")
            .Append("Exec=").Append(QuoteExecArgument(program)).Append('\n')
            .Append("Icon=zetl\n")
            .Append("Terminal=false\n")
            .Append("Categories=Utility;\n")
            .Append("StartupWMClass=Zetl\n")
            .Append("X-GNOME-Autostart-enabled=true\n")
            .ToString();
        var staging = path + ".tmp";
        File.WriteAllText(staging, content);
        File.Move(staging, path, overwrite: true);
    }

    public void Remove()
    {
        File.Delete(path);
    }

    private Dictionary<string, string>? ReadKeys()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var inEntry = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inEntry = line == "[Desktop Entry]";
                continue;
            }

            var equals = line.IndexOf('=');
            if (inEntry && equals > 0 && !line.StartsWith('#'))
            {
                keys.TryAdd(line[..equals].Trim(), line[(equals + 1)..].Trim());
            }
        }

        return keys;
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    // Desktop Entry Exec quoting: an argument with reserved characters is
    // double-quoted, escaping ", `, $ and \ inside the quotes.
    internal static string QuoteExecArgument(string argument)
    {
        if (argument.All(c => char.IsLetterOrDigit(c) || "/._-+".Contains(c)))
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        foreach (var c in argument)
        {
            if (c is '"' or '`' or '$' or '\\')
            {
                quoted.Append('\\');
            }

            quoted.Append(c);
        }

        return quoted.Append('"').ToString();
    }

    internal static string FirstExecArgument(string exec)
    {
        exec = exec.Trim();
        if (!exec.StartsWith('"'))
        {
            var space = exec.IndexOf(' ');
            return space > 0 ? exec[..space] : exec;
        }

        var argument = new StringBuilder();
        for (var i = 1; i < exec.Length; i++)
        {
            if (exec[i] == '\\' && i + 1 < exec.Length)
            {
                argument.Append(exec[++i]);
            }
            else if (exec[i] == '"')
            {
                break;
            }
            else
            {
                argument.Append(exec[i]);
            }
        }

        return argument.ToString();
    }
}
