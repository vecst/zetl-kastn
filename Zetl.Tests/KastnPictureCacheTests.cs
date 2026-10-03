using Avalonia.Headless.XUnit;
using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public class KastnPictureCacheTests
{
    [Fact]
    public async Task ConcurrentRequestsShareAFetchAndThenReuseContent()
    {
        var completion = new TaskCompletionSource<ZetlPictureContent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var cache = new KastnPictureCache((_, _) => { requests++; return completion.Task; });
        var first = cache.GetContentAsync("project", Slip("a"));
        var second = cache.GetContentAsync("project", Slip("a") with { Id = "another-slip" });
        Assert.Equal(1, requests);
        var picture = Content("a");
        completion.SetResult(picture);
        Assert.Same(picture, await first);
        Assert.Same(picture, await second);
        Assert.Same(picture, await cache.GetContentAsync("project", Slip("a")));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task ResetRejectsOldLoadsWithoutRemovingTheNewProjectsLoad()
    {
        var oldLoad = new TaskCompletionSource<ZetlPictureContent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newLoad = new TaskCompletionSource<ZetlPictureContent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var cache = new KastnPictureCache((project, _) =>
        {
            requests++;
            return project == "old" ? oldLoad.Task : newLoad.Task;
        });
        var stale = cache.GetContentAsync("old", Slip("a"));
        cache.Reset();
        var current = cache.GetContentAsync("new", Slip("a"));
        oldLoad.SetResult(Content("a"));
        Assert.Null(await stale);
        var sameCurrentLoad = cache.GetContentAsync("new", Slip("a"));
        Assert.Equal(2, requests);
        var picture = Content("a");
        newLoad.SetResult(picture);
        Assert.Same(picture, await current);
        Assert.Same(picture, await sameCurrentLoad);
    }

    [Fact]
    public async Task DisposalRejectsPendingResultsAndFurtherFetches()
    {
        var completion = new TaskCompletionSource<ZetlPictureContent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var cache = new KastnPictureCache((_, _) => { requests++; return completion.Task; });
        var pending = cache.GetContentAsync("project", Slip("a"));
        cache.Dispose();
        cache.Dispose();
        completion.SetResult(Content("a"));
        Assert.Null(await pending);
        Assert.Null(await cache.GetContentAsync("project", Slip("a")));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task ByteBudgetEvictsOldContentAndDoesNotRetainOversizedPictures()
    {
        var requests = new Dictionary<string, int>();
        using var cache = new KastnPictureCache((_, slip) =>
        {
            requests[slip.Id] = requests.GetValueOrDefault(slip.Id) + 1;
            return Task.FromResult<ZetlPictureContent?>(Content(slip.Id, slip.Id == "large" ? 5 : 3));
        }, contentBudget: 4);
        await cache.GetContentAsync("project", Slip("a"));
        await cache.GetContentAsync("project", Slip("b"));
        await cache.GetContentAsync("project", Slip("b"));
        Assert.Equal(1, requests["b"]);
        await cache.GetContentAsync("project", Slip("a"));
        Assert.Equal(2, requests["a"]);
        Assert.NotNull(await cache.GetContentAsync("project", Slip("large")));
        Assert.NotNull(await cache.GetContentAsync("project", Slip("large")));
        Assert.Equal(2, requests["large"]);
        await cache.GetContentAsync("project", Slip("a"));
        Assert.Equal(2, requests["a"]);
    }

    [Fact]
    public async Task InvalidContentAndFailedFetchesAreNotCached()
    {
        var attempts = 0;
        using var cache = new KastnPictureCache((_, _) => ++attempts switch
        {
            1 => Task.FromException<ZetlPictureContent?>(new IOException("disconnected")),
            2 => Task.FromResult<ZetlPictureContent?>(Content("wrong-hash")),
            3 => Task.FromResult<ZetlPictureContent?>(Content("a", 0)),
            _ => Task.FromResult<ZetlPictureContent?>(Content("a"))
        });
        await Assert.ThrowsAsync<IOException>(() => cache.GetContentAsync("project", Slip("a")));
        Assert.Null(await cache.GetContentAsync("project", Slip("a")));
        Assert.Null(await cache.GetContentAsync("project", Slip("a")));
        Assert.NotNull(await cache.GetContentAsync("project", Slip("a")));
        Assert.Equal(4, attempts);
    }

    [AvaloniaFact]
    public void DecodedImagesAreReusedByHashAndWidthAndReleasedOnReset()
    {
        using var cache = new KastnPictureCache((_, _) => Task.FromResult<ZetlPictureContent?>(null));
        var picture = Content("a") with
        {
            Bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")
        };
        var small = cache.Decode(picture, 260);
        Assert.Same(small, cache.Decode(picture, 260));
        Assert.Same(small, cache.FindDecoded("a", 260));
        Assert.NotSame(small, cache.Decode(picture, 1100));
        cache.Reset();
        Assert.Null(cache.FindDecoded("a", 260));
        Assert.NotSame(small, cache.Decode(picture, 260));
        cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => cache.Decode(picture, 260));
    }

    private static ZetlSlipSnapshot Slip(string sha) => new()
    {
        Id = sha, Revision = 1, Type = ZetlSlipType.Picture, BucketId = "bucket",
        Text = "", Source = "copy", CapturedAtUtc = DateTimeOffset.UnixEpoch,
        Picture = new() { Sha256 = sha }
    };

    private static ZetlPictureContent Content(string sha, int bytes = 3) => new()
    {
        SlipId = sha, Sha256 = sha, Width = 1, Height = 1, Bytes = new byte[bytes]
    };
}
