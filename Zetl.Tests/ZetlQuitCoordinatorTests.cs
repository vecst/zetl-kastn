using ZETL;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlQuitCoordinatorTests
{
    private sealed class Harness
    {
        public bool KastnConnected { get; set; } = true;
        public int Asks { get; private set; }
        public int Shutdowns { get; private set; }
        public List<string> Log { get; } = [];
        public TaskCompletionSource<KastnShutdownDecision> Decision { get; private set; } = new();
        public Exception? Throw { get; set; }

        public ZetlQuitCoordinator Create() => new(
            () => KastnConnected,
            () =>
            {
                Asks++;
                return Throw is { } ex ? Task.FromException<KastnShutdownDecision>(ex) : Decision.Task;
            },
            () => Shutdowns++,
            Log.Add);

        public void NextDecision() => Decision = new();
    }

    [Fact(DisplayName = "Quit with no Kastn shuts down without asking")]
    public async Task QuitWithoutKastnShutsDown()
    {
        var harness = new Harness { KastnConnected = false };
        await harness.Create().RequestAsync();

        AssertEqual(0, harness.Asks, "With no Kastn connected, there's nobody to ask.");
        AssertEqual(1, harness.Shutdowns, "Zetl shuts down.");
    }

    [Fact(DisplayName = "A repeated Quit waits on Kastn's pending decision instead of asking again")]
    public async Task RepeatedQuitSharesThePendingDecision()
    {
        var harness = new Harness();
        var quit = harness.Create();

        var first = quit.RequestAsync();
        AssertTrue(quit.IsWaitingOnKastn, "Kastn's dialog is open.");
        var second = quit.RequestAsync();

        AssertTrue(ReferenceEquals(first, second), "The second Quit joins the first.");
        AssertEqual(1, harness.Asks, "Kastn is asked once, so its busy channel is never hit with a second request.");
        AssertEqual(0, harness.Shutdowns, "Nothing shuts down while Kastn is deciding.");

        harness.Decision.SetResult(KastnShutdownDecision.Cancel);
        await second;
        AssertEqual(0, harness.Shutdowns, "Cancel in Kastn keeps Zetl running, for both requests.");
        AssertFalse(quit.IsWaitingOnKastn, "The decision is settled.");

        harness.NextDecision();
        var third = quit.RequestAsync();
        AssertEqual(2, harness.Asks, "A Quit after the cancel asks Kastn again.");
        harness.Decision.SetResult(KastnShutdownDecision.Close);
        await third;
        AssertEqual(1, harness.Shutdowns, "Kastn agreeing to close lets Zetl shut down, once.");
    }

    [Fact(DisplayName = "Quit still shuts down if asking Kastn fails")]
    public async Task QuitFailsOpenWhenTheSignalFails()
    {
        var harness = new Harness { Throw = new IOException("pipe broke") };
        await harness.Create().RequestAsync();

        AssertEqual(1, harness.Shutdowns, "A broken signal mustn't trap the user in an unquittable Zetl.");
        AssertTrue(harness.Log.Any(line => line.Contains("pipe broke", StringComparison.Ordinal)), "The failure is logged.");
    }
}
