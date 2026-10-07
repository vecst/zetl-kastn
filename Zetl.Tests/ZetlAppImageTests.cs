using Xunit;

namespace ZETL.Tests;

public sealed class ZetlAppImageTests : IDisposable
{
    private readonly string image = Path.GetTempFileName();

    public void Dispose() => File.Delete(image);

    [Fact]
    public void AProcessInsideTheMountRunsFromTheAppImage()
    {
        var mount = Path.Combine(Path.GetTempPath(), ".mount_ZetlAbc");
        var app = Path.Combine(mount, "usr", "lib", "zetl") + Path.DirectorySeparatorChar;
        Assert.Equal(image, ZetlAppImage.CurrentPath(image, mount, app));
    }

    [Fact]
    public void InheritedVariablesDoNotCountOutsideTheMount()
    {
        var mount = Path.Combine(Path.GetTempPath(), ".mount_ZetlAbc");
        var installed = Path.Combine(Path.GetTempPath(), "zetl-installed") + Path.DirectorySeparatorChar;
        Assert.Null(ZetlAppImage.CurrentPath(image, mount, installed));
        // A sibling whose name merely starts with the mount's is not inside it.
        Assert.Null(ZetlAppImage.CurrentPath(image, mount, Path.Combine(mount + "Other", "usr") + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void WithoutTheRuntimeVariablesThereIsNoAppImage()
    {
        Assert.Null(ZetlAppImage.CurrentPath(null, "/tmp/.mount_x", "/tmp/.mount_x/usr/"));
        Assert.Null(ZetlAppImage.CurrentPath(image, null, "/tmp/.mount_x/usr/"));
        Assert.Null(ZetlAppImage.CurrentPath(Path.Combine(Path.GetTempPath(), "missing.AppImage"), "/tmp/.mount_x", "/tmp/.mount_x/usr/"));
    }
}
