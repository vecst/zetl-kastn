using Xunit;

namespace ZETL.Tests;

public sealed class ZetlFontFamiliesTests
{
    private static readonly HashSet<string> WindowsFonts = new(StringComparer.OrdinalIgnoreCase)
        { "Segoe UI", "Cascadia Mono", "Consolas", "Georgia", "Arial" };
    private static readonly HashSet<string> LinuxFonts = new(StringComparer.OrdinalIgnoreCase)
        { "Noto Sans", "Noto Sans Mono", "Liberation Sans" };

    [Theory]
    [InlineData("Cascadia Mono,Consolas,monospace", "Cascadia Mono")]
    [InlineData("Consolas", "Consolas")]
    [InlineData("Inter", "Inter")]
    public void InstalledFamiliesResolveAsNamed(string requested, string expected) =>
        Assert.Equal(expected, ZetlFontFamilies.ResolveName(requested, WindowsFonts));

    [Theory]
    [InlineData("Cascadia Mono,Consolas,monospace", "monospace")]
    [InlineData("Consolas", "monospace")]
    [InlineData("Courier New", "monospace")]
    [InlineData("Georgia", "serif")]
    [InlineData("Arial", "Arial")]
    [InlineData("Inter", "Inter")]
    [InlineData("fonts:Inter#Inter", "fonts:Inter#Inter")]
    [InlineData("Missing Font, Noto Sans", "Noto Sans")]
    public void MissingFamiliesFallBackByKind(string requested, string expected) =>
        Assert.Equal(expected, ZetlFontFamilies.ResolveName(requested, LinuxFonts));

    [Fact]
    public void WithoutAFontListAuthoredNamesAreKept() =>
        Assert.Equal("Georgia", ZetlFontFamilies.ResolveName("Georgia", new HashSet<string>()));
}
