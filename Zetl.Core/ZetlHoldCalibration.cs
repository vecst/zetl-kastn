namespace ZETL;

// Turns measured press lengths into a hold threshold. Taps are presses meant
// as ordinary shortcuts; holds are presses meant to ask Zetl for something.
// A threshold works when every tap ends before it and every hold lasts past it.
internal static class ZetlHoldCalibration
{
    // Thresholds stay in the range Settings allows for the hold delay.
    public const double MinimumMs = 100;
    public const double MaximumMs = 2000;

    // How far across the gap from the slowest tap to the quickest hold the
    // suggestion sits. Real taps stretch longer than measured ones when the
    // user is distracted, and a false popup costs more than a slightly slower
    // hold, so the threshold leans toward the holds rather than the middle.
    public const double GapFraction = 0.7;

    // ...but never this close to the quickest hold, so every measured hold
    // still registers with room to spare.
    public const double HoldMarginMs = 25;

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
            ? Math.Min(
                slowestTap + GapFraction * (quickestHold - slowestTap),
                Math.Max(quickestHold - HoldMarginMs, (slowestTap + quickestHold) / 2))
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
