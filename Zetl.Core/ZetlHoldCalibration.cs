namespace ZETL;

// Turns measured press lengths into a hold threshold. Taps are presses meant
// as ordinary shortcuts; holds are presses meant to ask Zetl for something.
// A threshold works when every tap ends before it and every hold lasts past it.
internal static class ZetlHoldCalibration
{
    // Thresholds stay in the range Settings allows for the hold delay.
    public const double MinimumMs = 100;
    public const double MaximumMs = 2000;

    // How far across the gap from the slowest tap to the quick end of the holds
    // the suggestion sits. Real taps stretch longer than measured ones when the
    // user is distracted, and a false popup costs more than a slightly slower
    // hold, so the threshold leans toward the holds rather than the middle.
    public const double GapFraction = 0.7;

    // The quick end of the holds is this percentile rather than the single
    // quickest, so one or two early releases don't drag the threshold down.
    public const double QuickHoldPercentile = 0.2;

    // Measured taps come out quicker than real ones: asked for ten in a row,
    // people drum C with Ctrl held down. Never suggest under this multiple of
    // the slowest measured tap.
    public const double TapStretch = 2;

    // Below this a hold stops feeling deliberate, even when the taps would
    // allow it (users found 230 ms too twitchy and 280-290 right). It gives
    // way only when the holds themselves don't reach it.
    public const double DeliberateFloorMs = 250;

    // The holds decide how high the suggestion goes, up to here; slow taps can
    // still push it past.
    public const double HoldDrivenMaximumMs = 450;

    // The suggestion stays this far under the quick end of the holds, so they
    // register with room to spare.
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
        // Measured holds released before the suggestion.
        int SuggestedHoldsTooShort,
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
        var suggested = Math.Clamp(
            RoundTo5(separated ? Separating(slowestTap, holdMs) : LeastMisjudged(tapMs, holdMs)),
            MinimumMs,
            MaximumMs);

        return new Result(
            tapMs.Count,
            Median(tapMs),
            slowestTap,
            holdMs.Count,
            quickestHold,
            Median(holdMs),
            separated,
            suggested,
            holdMs.Count(ms => ms < suggested),
            tapMs.Count(ms => ms >= currentMs),
            holdMs.Count(ms => ms < currentMs));
    }

    // Every tap ended before every hold. Lean toward the holds, then keep the
    // suggestion clear of real-world taps and deliberate-feeling, without
    // crowding the quick end of the holds.
    private static double Separating(double slowestTap, IReadOnlyList<double> holdMs)
    {
        var quickHold = Percentile(holdMs, QuickHoldPercentile);
        var leaning = Math.Min(
            slowestTap + GapFraction * (quickHold - slowestTap),
            HoldDrivenMaximumMs);
        var floor = Math.Max(DeliberateFloorMs, TapStretch * slowestTap);
        // In a narrow gap the margin would sit on the taps; the middle wins.
        var ceiling = Math.Max(quickHold - HoldMarginMs, (slowestTap + quickHold) / 2);
        return Math.Min(Math.Max(leaning, floor), ceiling);
    }

    // Linear interpolation between the closest ranks.
    private static double Percentile(IReadOnlyList<double> values, double fraction)
    {
        var sorted = values.OrderBy(value => value).ToList();
        var rank = fraction * (sorted.Count - 1);
        var below = (int)Math.Floor(rank);
        var above = Math.Min(below + 1, sorted.Count - 1);
        return sorted[below] + (rank - below) * (sorted[above] - sorted[below]);
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
