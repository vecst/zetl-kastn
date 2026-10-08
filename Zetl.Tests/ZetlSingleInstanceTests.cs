using Xunit;

namespace ZETL.Tests;

public class ZetlSingleInstanceTests
{
    [Fact]
    public void WindowsKeepsOneInstancePerLogonSession() =>
        Assert.Equal(@"Local\ZetlSingleInstance", ZetlSingleInstance.MutexName("Zetl", windows: true, user: "lukas"));

    // An unprefixed name on Unix is per Unix session, which every launcher
    // starts afresh; only a Global name keeps one Zetl per user.
    [Fact]
    public void UnixKeepsOneInstancePerUserAcrossSessions() =>
        Assert.Equal(@"Global\KastnSingleInstance-lukas", ZetlSingleInstance.MutexName("Kastn", windows: false, user: "lukas"));
}
