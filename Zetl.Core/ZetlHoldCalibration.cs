namespace ZETL;

// Turns measured press lengths into a hold threshold. Taps are presses meant
// as ordinary shortcuts; holds are presses meant to ask Zetl for something.
// A threshold works when every tap ends before it and every hold lasts past it.
internal static class ZetlHoldCalibration
{
    // Thresholds stay in the range Settings allows for the hold delay.
    public const double MinimumMs = 100;
    public const double MaximumMs = 2000;

    public sealed record Result(
        int TapCount,
        double TapMedianMs,
        double SlowestTapMs,
        int HoldCount,
        double QuickestHoldMs,
        double HoldMedianMs,
        // True when every tap was shorter than every hold.
        bool Separated,
        double SuggestedMs,
        // Presses the current threshold would misjudge: taps long enough to
        // count as holds, and holds released before it.
        int CurrentTapsTooLong,
        int CurrentHoldsTooShort);

    public static Result Analyze(
        IReadOnlyList<double> tapMs,
        IReadOnlyList<double> holdMs,
        double currentMs)
    {
        if (tapMs.Count == 0 || holdMs.Count == 0)
        {
            throw new ArgumentException("Both taps and holds are needed.");
        }

        var slowestTap = tapMs.Max();
        var quickestHold = holdMs.Min();
        var separated = slowestTap < quickestHold;
        var suggested = separated
            // Halfway into the gap leaves equal room for a slower tap and a
            // quicker hold than any measured.
            ? (slowestTap + quickestHold) / 2
            : LeastMisjudged(tapMs, holdMs);

        return new Result(
            tapMs.Count,
            Median(tapMs),
            slowestTap,
            holdMs.Count,
            quickestHold,
            Median(holdMs),
            separated,
            Math.Clamp(RoundTo5(suggested), MinimumMs, MaximumMs),
            tapMs.Count(ms => ms >= currentMs),
            holdMs.Count(ms => ms < currentMs));
    }

    // With overlapping presses no threshold is perfect; pick the one that
    // misjudges the fewest, preferring the middle of a tied stretch.
    private static double LeastMisjudged(IReadOnlyList<double> tapMs, IReadOnlyList<double> holdMs)
    {
        var bestErrors = int.MaxValue;
        var bestStart = MinimumMs;
        var bestEnd = MinimumMs;
        for (var threshold = MinimumMs; threshold <= MaximumMs; threshold += 5)
        {
            var errors = tapMs.Count(ms => ms >= threshold) + holdMs.Count(ms => ms < threshold);
            if (errors < bestErrors)
            {
                bestErrors = errors;
                bestStart = bestEnd = threshold;
            }
            else if (errors == bestErrors && threshold == bestEnd + 5)
            {
                bestEnd = threshold;
            }
        }

        return (bestStart + bestEnd) / 2;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(value => value).ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static double RoundTo5(double ms) => Math.Round(ms / 5) * 5;
}
