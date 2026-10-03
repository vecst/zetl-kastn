using KASTN;
using Xunit;

namespace ZETL.Tests;

public class KastnRefreshCoordinatorTests
{
    [Fact]
    public async Task NestedBatchesFlushOnceAndCleanSynchronizationNeedsNoQuery()
    {
        var refreshes = 0;
        await using var coordinator = new KastnRefreshCoordinator(
            _ => { refreshes++; return Task.CompletedTask; },
            ex => throw new Exception("Unexpected refresh failure", ex), CancellationToken.None);
        var outer = coordinator.Defer();
        await using (coordinator.Defer())
        {
            for (var i = 0; i < 40; i++)
            {
                coordinator.Request();
                await coordinator.AfterMutationAsync();
            }
        }
        Assert.Equal(0, refreshes);
        Assert.True(await coordinator.FlushAsync());
        Assert.Equal(1, refreshes);
        Assert.False(await coordinator.FlushAsync());
        await outer.DisposeAsync();
        await outer.DisposeAsync();
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task LeavingAPartiallyCompletedBatchFlushesConfirmedChanges()
    {
        var refreshes = 0;
        await using var coordinator = new KastnRefreshCoordinator(
            _ => { refreshes++; return Task.CompletedTask; }, _ => { }, CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using var batch = coordinator.Defer();
            await coordinator.AfterMutationAsync();
            throw new IOException("second command failed");
        });
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task InvalidationDuringARefreshRemainsPending()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshes = 0;
        await using var coordinator = new KastnRefreshCoordinator(async token =>
        {
            if (++refreshes == 1)
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
            }
        }, _ => { }, CancellationToken.None);
        await using var batch = coordinator.Defer();
        await coordinator.AfterMutationAsync();
        var first = coordinator.FlushAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Request();
        release.SetResult();
        Assert.True(await first);
        Assert.True(await coordinator.FlushAsync());
        Assert.Equal(2, refreshes);
    }

    [Fact]
    public async Task ProjectionFailureDoesNotThrowFromAConfirmedMutationAndCanBeRetried()
    {
        var attempts = 0;
        Exception? reported = null;
        await using var coordinator = new KastnRefreshCoordinator(_ =>
            ++attempts == 1 ? Task.FromException(new IOException("projection failed")) : Task.CompletedTask,
            ex => reported = ex, CancellationToken.None);
        await coordinator.AfterMutationAsync();
        Assert.IsType<IOException>(reported);
        Assert.True(await coordinator.FlushAsync());
        Assert.False(await coordinator.FlushAsync());
        Assert.Equal(2, attempts);
    }
}
