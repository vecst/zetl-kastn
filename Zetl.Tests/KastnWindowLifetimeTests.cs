using KASTN;
using Xunit;

namespace ZETL.Tests;

public class KastnWindowLifetimeTests
{
    [Fact]
    public async Task TrayCloseKeepsResourcesLiveAndActivationRestoresWindow()
    {
        var h = new Harness { Tray = true };
        Assert.False(await h.Lifetime.RequestCloseAsync());
        Assert.Equal(1, h.Hidden);
        Assert.Equal(0, h.Prepared);
        Assert.Equal(0, h.Released);
        Assert.False(h.Lifetime.IsRetired);
        h.Lifetime.Activate();
        Assert.Equal(1, h.Shown);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatedCloseSharesOnePreparationAndAllowsRetry(bool saved)
    {
        var h = new Harness();
        var preparation = new TaskCompletionSource<bool>();
        h.Prepare = () => preparation.Task;
        var first = h.Lifetime.RequestCloseAsync();
        Assert.Same(first, h.Lifetime.RequestCloseAsync());
        Assert.False(h.Lifetime.AllowClose);
        Assert.False(await h.Lifetime.DecideShutdownAsync());
        h.Lifetime.Activate();
        Assert.Equal(0, h.Shown);
        preparation.SetResult(saved);
        Assert.Equal(saved, await first);
        Assert.Equal(saved ? 1 : 0, h.Closed);
        Assert.Equal(1, h.Prepared);
        if (!saved)
        {
            h.Prepare = () => Task.FromResult(true);
            Assert.True(await h.Lifetime.RequestCloseAsync());
            Assert.Equal(2, h.Prepared);
            Assert.Equal(1, h.Closed);
        }
    }

    [Fact]
    public async Task ShutdownApprovalWaitsForPipeConfirmationAndOverridesTraySetting()
    {
        var h = new Harness { Tray = true };
        var answer = new TaskCompletionSource<bool>();
        h.Confirm = () => answer.Task;
        var deciding = h.Lifetime.DecideShutdownAsync();
        Assert.Same(deciding, h.Lifetime.DecideShutdownAsync());
        Assert.False(await h.Lifetime.RequestCloseAsync());
        Assert.Equal(0, h.Hidden);
        answer.SetResult(true);
        Assert.True(await deciding);
        Assert.True(await h.Lifetime.DecideShutdownAsync());
        Assert.False(h.Lifetime.AllowClose);
        Assert.False(await h.Lifetime.RequestCloseAsync());
        Assert.Equal(0, h.Closed);
        Assert.Equal(1, h.Confirmed);
        Assert.Equal(1, h.Prepared);
        h.Lifetime.CloseConfirmed();
        h.Lifetime.CloseConfirmed();
        Assert.True(h.Lifetime.AllowClose);
        Assert.Equal(1, h.Closed);
        h.Lifetime.Dispose();
        h.Lifetime.Dispose();
        Assert.Equal(1, h.Released);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DeclinedOrUnsavedShutdownCanBeRetried(bool confirmed, bool saved)
    {
        var h = new Harness { Confirm = () => Task.FromResult(confirmed), Prepare = () => Task.FromResult(saved) };
        Assert.False(await h.Lifetime.DecideShutdownAsync());
        Assert.Equal(confirmed ? 1 : 0, h.Prepared);
        Assert.False(h.Lifetime.IsBusy);
        Assert.Equal(0, h.Closed);
        h.Confirm = () => Task.FromResult(true);
        h.Prepare = () => Task.FromResult(true);
        Assert.True(await h.Lifetime.DecideShutdownAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementCompletesPendingRequestsAndIgnoresLateDecisions(bool shutdown)
    {
        var h = new Harness();
        var response = new TaskCompletionSource<bool>();
        h.Prepare = h.Confirm = () => response.Task;
        var request = shutdown ? h.Lifetime.DecideShutdownAsync() : h.Lifetime.RequestCloseAsync();
        h.Lifetime.Dispose();
        Assert.False(await request.WaitAsync(TimeSpan.FromSeconds(1)));
        response.SetResult(true);
        await Task.Yield();
        Assert.Equal(0, h.Closed);
        Assert.Equal(shutdown ? 0 : 1, h.Prepared);
        h.Lifetime.Activate();
        h.Lifetime.CloseConfirmed();
        Assert.False(await h.Lifetime.DecideShutdownAsync());
        Assert.False(await h.Lifetime.RequestCloseAsync());
        Assert.Equal(1, h.Released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedPreparationFailuresAreObservedAndDoNotPoisonRetry(bool shutdown)
    {
        var h = new Harness { Prepare = () => throw new IOException("failed") };
        Assert.False(await (shutdown ? h.Lifetime.DecideShutdownAsync() : h.Lifetime.RequestCloseAsync()));
        Assert.Single(h.Errors);
        Assert.False(h.Lifetime.AllowClose);
        Assert.False(h.Lifetime.IsBusy);
        h.Prepare = () => Task.FromResult(true);
        Assert.True(await (shutdown ? h.Lifetime.DecideShutdownAsync() : h.Lifetime.RequestCloseAsync()));
    }

    [Fact]
    public async Task UndeliveredApprovalCanBeAbandonedAndRetried()
    {
        var h = new Harness();
        Assert.True(await h.Lifetime.DecideShutdownAsync());
        Assert.False(h.Lifetime.CanActivate);
        h.Lifetime.AbandonShutdown();
        Assert.True(h.Lifetime.CanActivate);
        h.Lifetime.Activate();
        Assert.Equal(2, h.Shown);
        Assert.True(await h.Lifetime.DecideShutdownAsync());
        Assert.Equal(2, h.Confirmed);
        Assert.Equal(0, h.Closed);
    }

    private sealed class Harness
    {
        public bool Tray;
        public int Shown, Hidden, Closed, Prepared, Confirmed, Released;
        public Func<Task<bool>> Prepare = () => Task.FromResult(true);
        public Func<Task<bool>> Confirm = () => Task.FromResult(true);
        public List<Exception> Errors = [];
        public KastnWindowLifetime Lifetime { get; }
        public Harness() => Lifetime = new(() => Tray, () => Shown++, () => Hidden++,
            () => { Prepared++; return Prepare(); }, () => { Confirmed++; return Confirm(); },
            () => Closed++, () => Released++, Errors.Add);
    }
}
