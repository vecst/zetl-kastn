using Xunit;

namespace ZETL.Tests;

public sealed class HyprlandIpcTests
{
    [Fact]
    public void ReadsTheCursorFromHyprlandsReply() =>
        Assert.Equal((1022, 355), HyprlandIpc.ParseCursor("\n{\n    \"x\": 1022,\n    \"y\": 355\n}\n"));

    [Theory]
    [InlineData("")]
    [InlineData("ok")]
    [InlineData("[]")]
    [InlineData("{\"x\": 1}")]
    [InlineData("{\"x\": \"a\", \"y\": 2}")]
    public void AnythingElseIsNoCursor(string reply) => Assert.Null(HyprlandIpc.ParseCursor(reply));
}
