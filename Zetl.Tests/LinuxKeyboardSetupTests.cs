using Xunit;

namespace ZETL.Tests;

// The advice Zetl gives when it can't take the keyboard on Linux, from the
// cases seen on a fresh Ubuntu install.
public class LinuxKeyboardSetupTests
{
    private const string Groups = "root:x:0:\nadm:x:4:syslog,lukas\ninput:x:994:{0}\nlukas:x:1000:\n";

    private static string Status(params uint[] groups) =>
        $"Name:\tZetl\nUid:\t1000\t1000\t1000\t1000\nGroups:\t{string.Join(' ', groups)} \nVmPeak:\t1 kB\n";

    [Fact]
    public void NotInTheGroupYetSaysToJoinAndRestart()
    {
        var advice = LinuxKeyboardSetup.Explain("lukas", string.Format(Groups, ""), Status(4, 1000), uinputDenied: true);
        Assert.Contains("isn't in the 'input' group yet", advice);
        Assert.Contains("restart your computer", advice);
    }

    [Fact]
    public void JoinedButNotInThisSessionSaysRestart()
    {
        // usermod ran, but the session (and the systemd user manager that started
        // it) predates it: on Ubuntu that survived logging out.
        var advice = LinuxKeyboardSetup.Explain("lukas", string.Format(Groups, "lukas"), Status(4, 1000), uinputDenied: true);
        Assert.Contains("Zetl isn't running with it yet", advice);
        Assert.Contains("Restart your computer", advice);
    }

    [Fact]
    public void InTheGroupButUinputClosedSaysInstallTheRule()
    {
        var advice = LinuxKeyboardSetup.Explain("lukas", string.Format(Groups, "syslog,lukas"), Status(4, 994, 1000), uinputDenied: true);
        Assert.Contains("70-zetl-uinput.rules", advice);
    }

    [Fact]
    public void InTheGroupButKeyboardsClosedPointsAtTheLog()
    {
        var advice = LinuxKeyboardSetup.Explain("lukas", string.Format(Groups, "lukas"), Status(994), uinputDenied: false);
        Assert.Contains("diagnostics log", advice);
    }

    [Fact]
    public void NoInputGroupAtAllIsSaidPlainly()
    {
        var advice = LinuxKeyboardSetup.Explain("lukas", "root:x:0:\nlukas:x:1000:\n", Status(1000), uinputDenied: true);
        Assert.Contains("no 'input' group", advice);
    }
}
