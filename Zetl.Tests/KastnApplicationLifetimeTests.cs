using KASTN;
using Xunit;
using ZETL;

namespace ZETL.Tests;

public class KastnApplicationLifetimeTests
{
    [Fact]
    public async Task QueuedActivationsAndConfirmationsRetireBeforeResourceDisposal()
    {
        await using var server = new KastnControlServer($"lifetime-{Guid.NewGuid():N}");
        var queue = new Queue<Action>();
        var calls = new List<string>();
        var release = new TaskCompletionSource();
        var lifetime = new KastnApplicationLifetime(server,
            id => { calls.Add($"activate {id}"); return Task.CompletedTask; },
            () => Task.FromResult(true), () => calls.Add("stop"), () => calls.Add("close"),
            () => calls.Add("window"), () => calls.Add("theme"),
            () => { calls.Add("connection"); return new(release.Task); }, queue.Enqueue,
            _ => calls.Add("error"), () => calls.Add("abandon"));
        lifetime.RequestActivation("project");
        var confirmed = server.ShutdownConfirmed!;
        confirmed();
        var decision = server.ShutdownRequested!;
        var disposing = lifetime.DisposeAsync();
        Assert.Null(server.ShutdownRequested);
        Assert.Null(server.ShutdownConfirmed);
        Assert.Null(server.ShutdownAbandoned);
        Assert.False(await decision());
        while (queue.TryDequeue(out var work)) work();
        Assert.Equal(new[] { "stop", "window", "theme", "connection" }, calls);
        Assert.Same(disposing.AsTask(), lifetime.DisposeAsync().AsTask());
        release.SetResult();
        await disposing;
    }

    [Fact]
    public async Task ConfirmedShutdownStopsRelaunchBeforeClosingAndOnlyOnce()
    {
        await using var server = new KastnControlServer($"lifetime-{Guid.NewGuid():N}");
        var calls = new List<string>();
        var queue = new Queue<Action>();
        await using var lifetime = new KastnApplicationLifetime(server, _ => Task.CompletedTask,
            () => Task.FromResult(true), () => calls.Add("stop"), () => calls.Add("close"),
            () => { }, () => { }, () => ValueTask.CompletedTask, queue.Enqueue, _ => { }, () => { });
        Assert.True(await server.ShutdownRequested!());
        Assert.Empty(calls);
        server.ShutdownConfirmed!();
        server.ShutdownConfirmed!();
        while (queue.TryDequeue(out var work)) work();
        Assert.Equal(new[] { "stop", "close" }, calls);
    }

    [Fact]
    public async Task ActivationErrorsAfterRetirementCannotReportIntoTheOldWindow()
    {
        var response = new TaskCompletionSource();
        var errors = new List<Exception>();
        var lifetime = new KastnApplicationLifetime(null, _ => response.Task,
            () => Task.FromResult(false), () => { }, () => { }, () => { }, () => { },
            () => ValueTask.CompletedTask, work => work(), errors.Add, () => { });
        lifetime.RequestActivation("project");
        await lifetime.DisposeAsync();
        response.SetException(new IOException("late failure"));
        await Task.Yield();
        Assert.Empty(errors);
    }

    [Fact]
    public async Task CleanupFailureStillReleasesThemeWatcherAndConnection()
    {
        var calls = new List<string>();
        var lifetime = new KastnApplicationLifetime(null, _ => Task.CompletedTask,
            () => Task.FromResult(false), () => calls.Add("stop"), () => { },
            () => throw new IOException("window cleanup"), () => calls.Add("theme"),
            () => { calls.Add("connection"); return ValueTask.CompletedTask; }, work => work(), _ => { }, () => { });
        await Assert.ThrowsAsync<IOException>(() => lifetime.DisposeAsync().AsTask());
        Assert.Equal(new[] { "stop", "theme", "connection" }, calls);
    }

    [Fact]
    public async Task ServerDisposalDoesNotWaitForeverForAnUnfinishedUiDecision()
    {
        var pipe = $"lifetime-{Guid.NewGuid():N}";
        var server = new KastnControlServer(pipe);
        var arrived = new TaskCompletionSource();
        var answer = new TaskCompletionSource<bool>();
        server.ShutdownRequested = () => { arrived.TrySetResult(); return answer.Task; };
        server.Start();
        var request = KastnControlChannel.RequestShutdownAsync(pipe, TimeSpan.FromSeconds(2));
        try
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(answer.Task.IsCompleted);
            // The client sees the closed pipe rather than a completed decision.
            try { await request; } catch (IOException) { }
        }
        finally { answer.TrySetResult(false); }
    }

    [Fact]
    public async Task DisconnectedRequesterReleasesApprovalWithoutConfirmingShutdown()
    {
        var pipe = $"lifetime-{Guid.NewGuid():N}";
        await using var server = new KastnControlServer(pipe);
        var arrived = new TaskCompletionSource();
        var answer = new TaskCompletionSource<bool>();
        var abandoned = new TaskCompletionSource();
        var confirmed = false;
        server.ShutdownRequested = () => { arrived.TrySetResult(); return answer.Task; };
        server.ShutdownConfirmed = () => confirmed = true;
        server.ShutdownAbandoned = () => abandoned.TrySetResult();
        server.Start();
        using (var client = new System.IO.Pipes.NamedPipeClientStream(".", pipe,
            System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
        {
            await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
            using var writer = new StreamWriter(client, new System.Text.UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(
                new KastnControlRequest(Command: KastnControlChannel.ShutdownCommand)));
            await writer.FlushAsync();
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        answer.SetResult(true);
        await abandoned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(confirmed);
        server.ShutdownRequested = () => Task.FromResult(false);
        Assert.Equal(KastnShutdownDecision.Cancel,
            await KastnControlChannel.RequestShutdownAsync(pipe, TimeSpan.FromSeconds(2)));
    }
}
