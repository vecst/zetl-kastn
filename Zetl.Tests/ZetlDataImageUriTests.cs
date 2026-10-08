using Xunit;

namespace ZETL.Tests;

public class ZetlDataImageUriTests
{
    // A 1x1 PNG.
    private const string Png =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    [Fact]
    public void DecodesABase64ImageUri()
    {
        Assert.True(ZetlDataImageUri.TryDecode($"  data:image/PNG;base64,{Png}\n", 1024, out var bytes, out var type));
        Assert.Equal("image/png", type);
        Assert.Equal(Convert.FromBase64String(Png), bytes);
    }

    [Fact]
    public void AcceptsParametersAndLineBreaksInThePayload()
    {
        var wrapped = Png[..40] + "\r\n" + Png[40..];
        Assert.True(ZetlDataImageUri.TryDecode($"data:image/png;name=dot.png;base64,{wrapped}", 1024, out var bytes, out _));
        Assert.Equal(Convert.FromBase64String(Png), bytes);
    }

    [Theory]
    [InlineData("data:text/plain;base64,aGVsbG8=")]          // not an image
    [InlineData("data:image/png,%89PNG")]                    // not base64
    [InlineData("data:image/png;base64,***not base64***")]   // malformed
    [InlineData("data:image/png;base64,")]                   // empty
    [InlineData("data:image/png;base64")]                    // no payload
    [InlineData("see data:image/png;base64,AAAA")]           // not the whole copy
    [InlineData("https://example.com/a.png")]
    public void LeavesEverythingElseAsText(string text) =>
        Assert.False(ZetlDataImageUri.TryDecode(text, 1024, out _, out _));

    [Fact]
    public void RefusesAPayloadOverTheSizeCap()
    {
        var big = Convert.ToBase64String(new byte[4096]);
        Assert.False(ZetlDataImageUri.TryDecode($"data:image/png;base64,{big}", 1024, out _, out _));
        Assert.True(ZetlDataImageUri.TryDecode($"data:image/png;base64,{big}", 4096, out _, out _));
    }
}
