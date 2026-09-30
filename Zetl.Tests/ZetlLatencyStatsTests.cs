using System.Diagnostics;
using ZETL;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlLatencyStatsTests
{
    private static long Ms(double milliseconds) =>
        (long)(milliseconds * Stopwatch.Frequency / 1000.0);

    [Fact] public void SummaryBucketsSamplesAndReportsTheMaximum()
    {
        var stats = new ZetlLatencyStats("key hook");
        stats.Record(Ms(0.01));
        stats.Record(Ms(0.02));
        stats.Record(Ms(0.3));
        stats.Record(Ms(75));

        AssertEqual(
            "key hook: n=4, max 75.00 ms; <=0.05 ms 2, <=0.5 ms 1, >50 ms 1",
            stats.TakeSummary(),
            "The summary counts each sample in its bucket and names the slowest.");
    }

    [Fact] public void TakingASummaryStartsANewWindow()
    {
        var stats = new ZetlLatencyStats("hold");
        AssertEqual<string?>(null, stats.TakeSummary(), "An empty window has no summary.");

        stats.Record(Ms(1.5));
        AssertTrue(stats.TakeSummary() is not null, "A recorded sample is summarized.");
        AssertEqual<string?>(null, stats.TakeSummary(), "The next window starts empty.");
    }

    [Fact] public void ConcurrentRecordsAreAllCounted()
    {
        var stats = new ZetlLatencyStats("key hook");
        Parallel.For(0, 10_000, _ => stats.Record(Ms(0.01)));

        AssertTrue(
            stats.TakeSummary()!.StartsWith("key hook: n=10000,", StringComparison.Ordinal),
            "Records from several threads are never lost.");
    }
}
