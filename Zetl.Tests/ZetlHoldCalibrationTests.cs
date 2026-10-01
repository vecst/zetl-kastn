using ZETL;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlHoldCalibrationTests
{
    [Fact] public void SeparatedPressesSuggestTheMiddleOfTheGap()
    {
        var result = ZetlHoldCalibration.Analyze(
            tapMs: [80, 95, 110, 120, 180],
            holdMs: [420, 500, 610, 700],
            currentMs: 353);

        AssertTrue(result.Separated, "Every tap ended before every hold.");
        AssertEqual(110.0, result.TapMedianMs, "Median tap.");
        AssertEqual(180.0, result.SlowestTapMs, "Slowest tap.");
        AssertEqual(420.0, result.QuickestHoldMs, "Quickest hold.");
        AssertEqual(300.0, result.SuggestedMs, "Halfway between 180 and 420.");
        AssertEqual(0, result.CurrentTapsTooLong, "353 lets every tap through.");
        AssertEqual(0, result.CurrentHoldsTooShort, "353 catches every hold.");
    }

    [Fact] public void OverlappingPressesSuggestTheLeastMisjudgedThreshold()
    {
        var result = ZetlHoldCalibration.Analyze(
            tapMs: [90, 100, 110, 400],
            holdMs: [300, 500, 600],
            currentMs: 353);

        AssertFalse(result.Separated, "A 400 ms tap is longer than a 300 ms hold.");
        AssertEqual(1, result.CurrentTapsTooLong, "353 would turn the 400 ms tap into a hold.");
        AssertEqual(1, result.CurrentHoldsTooShort, "353 would miss the 300 ms hold.");
        // Between 115 and 300 only the 400 ms tap is misjudged; past 400 the
        // 300 ms hold is too, so the suggestion sits in the first stretch.
        AssertTrue(
            result.SuggestedMs > 110 && result.SuggestedMs <= 300,
            $"The suggestion misjudges only one press (got {result.SuggestedMs}).");
    }

    [Fact] public void SuggestionsStayInTheAllowedRangeAndRoundToFive()
    {
        var quick = ZetlHoldCalibration.Analyze([20, 25], [60, 70], 353);
        AssertEqual(ZetlHoldCalibration.MinimumMs, quick.SuggestedMs, "Never below the minimum hold delay.");

        var odd = ZetlHoldCalibration.Analyze([101], [402], 353);
        AssertEqual(250.0, odd.SuggestedMs, "251.5 rounds to the nearest 5 ms.");
    }
}
