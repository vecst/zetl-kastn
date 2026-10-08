using Microsoft.Win32;
using Xunit;

namespace ZETL.Tests;

public sealed class ZetlStartAtLoginTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "ZetlStartAtLoginTests", Guid.NewGuid().ToString("N"));
    private readonly string testKey = $@"Software\ZetlTests\StartAtLogin-{Guid.NewGuid():N}";

    public ZetlStartAtLoginTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void StartsTheAppImageOrThePublishedProgramNeverDotnet()
    {
        Assert.Equal("/home/a/Zetl.AppImage", ZetlStartAtLogin.LaunchCommand("/home/a/Zetl.AppImage", "/tmp/.mount/usr/lib/zetl/Zetl", windows: false));
        Assert.Equal(@"C:\Apps\Zetl\Zetl.exe", ZetlStartAtLogin.LaunchCommand(null, @"C:\Apps\Zetl\Zetl.exe", windows: true));
        Assert.Equal("/opt/zetl/Zetl", ZetlStartAtLogin.LaunchCommand(null, "/opt/zetl/Zetl", windows: false));
        Assert.Null(ZetlStartAtLogin.LaunchCommand(null, @"C:\Program Files\dotnet\dotnet.exe", windows: true));
        Assert.Null(ZetlStartAtLogin.LaunchCommand(null, "/usr/bin/dotnet", windows: false));
    }

    [Fact]
    public void XdgEntryTurnsOnAndOffAndQuotesPaths()
    {
        var program = Path.Combine(directory, "My Apps", "Zetl");
        var path = Path.Combine(directory, "autostart", "zetl.desktop");
        var login = new ZetlStartAtLogin(new ZetlXdgAutostartEntry(path), program);

        Assert.True(login.IsAvailable);
        Assert.False(login.IsEnabled);
        login.Set(true);
        Assert.True(login.IsEnabled);
        Assert.Contains($"Exec=\"{program.Replace("\\", "\\\\")}\"", File.ReadAllText(path));
        Assert.Equal(program, new ZetlXdgAutostartEntry(path).RegisteredProgram());
        login.Set(false);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("Hidden=true")]
    [InlineData("X-GNOME-Autostart-enabled=false")]
    public void XdgEntryDisabledByTheDesktopCountsAsOff(string disabled)
    {
        var path = Path.Combine(directory, "zetl.desktop");
        File.WriteAllText(path, $"[Desktop Entry]\nType=Application\nExec=/opt/zetl/Zetl\n{disabled}\n");
        Assert.False(new ZetlXdgAutostartEntry(path).IsEnabled());
    }

    [Fact]
    public void ReadsTheEntryInstallShWrites()
    {
        var path = Path.Combine(directory, "zetl.desktop");
        File.WriteAllText(path, "[Desktop Entry]\nType=Application\nName=Zetl\nExec=/home/lukas/.local/share/zetl/Zetl\nIcon=zetl\n");
        var entry = new ZetlXdgAutostartEntry(path);
        Assert.True(entry.IsEnabled());
        Assert.Equal("/home/lukas/.local/share/zetl/Zetl", entry.RegisteredProgram());
    }

    [Fact]
    public void RepairsOnlyAnEntryWhoseProgramIsGone()
    {
        var path = Path.Combine(directory, "zetl.desktop");
        var current = Path.Combine(directory, "new", "Zetl");
        var login = new ZetlStartAtLogin(new ZetlXdgAutostartEntry(path), current);

        // Another copy that still exists keeps login: a test build never takes over.
        var other = Path.Combine(directory, "Zetl-other");
        File.WriteAllText(other, "");
        new ZetlXdgAutostartEntry(path).Write(other);
        Assert.False(login.RepairIfMoved());
        Assert.Equal(other, new ZetlXdgAutostartEntry(path).RegisteredProgram());

        // The folder moved: the old program is gone, so this copy takes over.
        File.Delete(other);
        Assert.True(login.RepairIfMoved());
        Assert.Equal(current, new ZetlXdgAutostartEntry(path).RegisteredProgram());

        // Turned off stays off.
        login.Set(false);
        Assert.False(login.RepairIfMoved());
        Assert.False(File.Exists(path));
    }

    [WindowsOnlyFact]
    public void WindowsRunEntryRespectsTaskManagersSwitch()
    {
        if (!OperatingSystem.IsWindows()) return;
        var runKey = testKey + @"\Run";
        var approvedKey = testKey + @"\StartupApproved";
        var entry = new ZetlWindowsRunEntry(runKey, approvedKey);
        var login = new ZetlStartAtLogin(entry, @"C:\Apps\Zetl\Zetl.exe");

        login.Set(true);
        Assert.True(login.IsEnabled);
        using (var run = Registry.CurrentUser.OpenSubKey(runKey))
        {
            Assert.Equal("\"C:\\Apps\\Zetl\\Zetl.exe\"", run!.GetValue("Zetl"));
        }
        Assert.Equal(@"C:\Apps\Zetl\Zetl.exe", entry.RegisteredProgram());

        // Task Manager's Startup apps page disables without deleting.
        using (var approved = Registry.CurrentUser.CreateSubKey(approvedKey))
        {
            approved.SetValue("Zetl", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        }
        Assert.False(login.IsEnabled);

        // Turning it on from Zetl clears that switch.
        login.Set(true);
        Assert.True(login.IsEnabled);

        login.Set(false);
        Assert.False(login.IsEnabled);
        Assert.Null(entry.RegisteredProgram());
    }
}
