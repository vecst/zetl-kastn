using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public partial class KastnPictureCacheTests
{
    [AvaloniaFact]
    public void DecodedBudgetEvictsLeastRecentlyReleasedIdleImage()
    {
        var unit = PictureUnitBytes();
        using var cache = new KastnPictureCache((_, _) => Task.FromResult<ZetlPictureContent?>(null), decodedBudget: unit * 2);
        cache.AcquireDecoded(DecodedPicture("a"), 8).Dispose();
        cache.AcquireDecoded(DecodedPicture("b"), 8).Dispose();
        cache.TryAcquireDecoded("a", 8)!.Dispose();
        using var current = cache.AcquireDecoded(DecodedPicture("c"), 8);
        Assert.NotNull(cache.FindDecoded("a", 8));
        Assert.Null(cache.FindDecoded("b", 8));
        Assert.Same(current.Bitmap, cache.FindDecoded("c", 8));
        Assert.Equal(unit * 2, cache.DecodedResidentBytes);
        Assert.Equal(unit, cache.PinnedDecodedBytes);
        Assert.Equal(1, cache.DecodedEvictions);
    }

    [AvaloniaFact]
    public void PinnedImagesMayExceedBudgetUntilControlsReleaseThem()
    {
        var unit = PictureUnitBytes();
        using var cache = new KastnPictureCache((_, _) => Task.FromResult<ZetlPictureContent?>(null), decodedBudget: unit);
        using var first = cache.AcquireDecoded(DecodedPicture("a"), 8);
        var second = cache.AcquireDecoded(DecodedPicture("b"), 8);
        Assert.Equal(unit * 2, cache.DecodedResidentBytes);
        Assert.Equal(unit * 2, cache.PinnedDecodedBytes);
        second.Dispose();
        Assert.Null(cache.FindDecoded("b", 8));
        Assert.Same(first.Bitmap, cache.FindDecoded("a", 8));
        Assert.Equal(unit, cache.DecodedResidentBytes);
        using var shared = cache.TryAcquireDecoded("a", 8)!;
        Assert.Same(first.Bitmap, shared.Bitmap);
        Assert.Equal(2, cache.DecodeCount);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetOrCloseRetiresLeasedImagesUntilTheirLastRelease(bool close)
    {
        using var cache = new KastnPictureCache((_, _) => Task.FromResult<ZetlPictureContent?>(null), decodedBudget: 0);
        var first = cache.AcquireDecoded(DecodedPicture("a"), 8);
        var second = cache.TryAcquireDecoded("a", 8)!;
        var size = first.Bitmap.PixelSize;
        if (close) cache.Dispose(); else cache.Reset();
        Assert.Null(cache.FindDecoded("a", 8));
        Assert.Equal(size, first.Bitmap.PixelSize);
        Assert.Equal(size, second.Bitmap.PixelSize);
        first.Dispose();
        first.Dispose();
        Assert.Equal(1, cache.DecodedCount);
        Assert.Equal(size, second.Bitmap.PixelSize);
        second.Dispose();
        Assert.Equal(0, cache.DecodedCount);
        Assert.Equal(0, cache.DecodedResidentBytes);
        Assert.Throws<ObjectDisposedException>(() => first.Bitmap);
    }

    [AvaloniaFact]
    public void OversizedDecodedImageIsUsableButNotRetainedAfterRelease()
    {
        using var cache = new KastnPictureCache((_, _) => Task.FromResult<ZetlPictureContent?>(null), decodedBudget: PictureUnitBytes() - 1);
        var lease = cache.AcquireDecoded(DecodedPicture("large"), 8);
        Assert.True(lease.Bitmap.PixelSize.Width > 0);
        lease.Dispose();
        Assert.Equal(0, cache.DecodedResidentBytes);
        Assert.Null(cache.TryAcquireDecoded("large", 8));
        cache.AcquireDecoded(DecodedPicture("large"), 8).Dispose();
        Assert.Equal(2, cache.DecodeCount);
        Assert.Equal(2, cache.DecodedEvictions);
    }

    [AvaloniaFact]
    public async Task ConcurrentAsyncMissesDecodeOnceOnABackgroundThread()
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var workerThread = uiThread;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cache = new KastnPictureCache((_, _) => Task.FromResult<ZetlPictureContent?>(null), decode: (picture, width) =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Decode not released.");
            return DecodeFixture(picture, width);
        });
        var tasks = Enumerable.Range(0, 8).Select(_ => cache.AcquireDecodedAsync(DecodedPicture("a"), 8, CancellationToken.None)).ToArray();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(uiThread, workerThread);
            Assert.All(tasks, task => Assert.False(task.IsCompleted));
        }
        finally { release.Set(); }
        var leases = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.All(leases, lease => Assert.Same(leases[0].Bitmap, lease.Bitmap));
            Assert.Equal(1, cache.DecodeCount);
            Assert.Equal(PictureUnitBytes(), cache.PinnedDecodedBytes);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
        Assert.Equal(0, cache.PinnedDecodedBytes);
    }

    [AvaloniaFact]
    public async Task CancelledQueuedDecodeNeverAllocatesItsBitmap()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        using var cache = new KastnPictureCache((_, _) => Task.FromResult<ZetlPictureContent?>(null), decode: (picture, width) =>
        {
            Interlocked.Increment(ref attempts);
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Decode not released.");
            return DecodeFixture(picture, width);
        });
        var first = cache.AcquireDecodedAsync(DecodedPicture("a"), 8, CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = cache.AcquireDecodedAsync(DecodedPicture("b"), 8, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Equal(1, attempts);
            Assert.Null(cache.FindDecoded("b", 8));
        }
        finally { release.Set(); }
        (await first.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetOrCloseRejectsRunningAndQueuedDecodes(bool close)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var attempts = 0;
        using var cache = new KastnPictureCache((_, _) => Task.FromResult<ZetlPictureContent?>(null), decode: (picture, width) =>
        {
            Interlocked.Increment(ref attempts);
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Decode not released.");
            return DecodeFixture(picture, width);
        });
        var first = cache.AcquireDecodedAsync(DecodedPicture("a"), 8, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = cache.AcquireDecodedAsync(DecodedPicture("b"), 8, CancellationToken.None);
        if (close) cache.Dispose(); else cache.Reset();
        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, attempts);
        Assert.Equal(0, cache.DecodedCount);
        Assert.Equal(0, cache.DecodedResidentBytes);
        if (!close)
        {
            using var current = await cache.AcquireDecodedAsync(DecodedPicture("b"), 8, CancellationToken.None);
            Assert.NotNull(current.Bitmap);
        }
    }

    [AvaloniaFact]
    public async Task FailedAsyncDecodeReleasesItsSlotAndCanRetry()
    {
        var attempts = 0;
        using var cache = new KastnPictureCache((_, _) => Task.FromResult<ZetlPictureContent?>(null), decode: (picture, width) =>
            ++attempts == 1 ? throw new IOException("Bad image") : DecodeFixture(picture, width));
        await Assert.ThrowsAsync<IOException>(() => cache.AcquireDecodedAsync(DecodedPicture("a"), 8, CancellationToken.None));
        Assert.Null(cache.FindDecoded("a", 8));
        using var lease = await cache.AcquireDecodedAsync(DecodedPicture("a"), 8, CancellationToken.None);
        Assert.NotNull(lease.Bitmap);
        Assert.Equal(1, cache.DecodeCount);
    }

    private static ZetlPictureContent DecodedPicture(string sha) => Content(sha) with
    {
        Bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")
    };
    private static Bitmap DecodeFixture(ZetlPictureContent picture, int width)
    {
        using var stream = new MemoryStream(picture.Bytes, writable: false);
        return Bitmap.DecodeToWidth(stream, width);
    }
    private static long PictureUnitBytes()
    {
        using var bitmap = DecodeFixture(DecodedPicture("unit"), 8);
        return (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
    }
}
