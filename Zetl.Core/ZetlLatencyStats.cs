using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ZETL;

// A latency histogram cheap enough for the keyboard hook: Record is a handful of
// interlocked operations and never allocates. TakeSummary reports the window
// since the last summary and starts a new one.
internal sealed class ZetlLatencyStats(string name)
{
    private static readonly double[] BucketLimitsMs = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 50];
    private readonly long[] buckets = new long[BucketLimitsMs.Length + 1];
    private long count;
    private long maxTicks;

    public string Name { get; } = name;

    public static double ToMilliseconds(long stopwatchTicks) =>
        stopwatchTicks * 1000.0 / Stopwatch.Frequency;

    public void Record(long elapsedTicks)
    {
        var milliseconds = ToMilliseconds(elapsedTicks);
        var bucket = 0;
        while (bucket < BucketLimitsMs.Length && milliseconds > BucketLimitsMs[bucket])
        {
            bucket++;
        }

        Interlocked.Increment(ref buckets[bucket]);
        Interlocked.Increment(ref count);
        var observed = Volatile.Read(ref maxTicks);
        while (elapsedTicks > observed)
        {
            var previous = Interlocked.CompareExchange(ref maxTicks, elapsedTicks, observed);
            if (previous == observed)
            {
                break;
            }

            observed = previous;
        }
    }

    // e.g. "key hook: n=1523, max 0.41 ms; <=0.05 ms 1490, <=0.1 ms 28, <=0.5 ms 5".
    // Null when nothing was recorded since the last summary.
    public string? TakeSummary()
    {
        var total = Interlocked.Exchange(ref count, 0);
        var max = Interlocked.Exchange(ref maxTicks, 0);
        var counts = new long[buckets.Length];
        for (var i = 0; i < buckets.Length; i++)
        {
            counts[i] = Interlocked.Exchange(ref buckets[i], 0);
        }

        if (total == 0)
        {
            return null;
        }

        var summary = new StringBuilder();
        summary.Append(CultureInfo.InvariantCulture, $"{Name}: n={total}, max {ToMilliseconds(max):0.00} ms;");
        var first = true;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0)
            {
                continue;
            }

            summary.Append(first ? " " : ", ");
            first = false;
            summary.Append(i < BucketLimitsMs.Length
                ? string.Create(CultureInfo.InvariantCulture, $"<={BucketLimitsMs[i]} ms {counts[i]}")
                : string.Create(CultureInfo.InvariantCulture, $">{BucketLimitsMs[^1]} ms {counts[i]}"));
        }

        return summary.ToString();
    }
}
