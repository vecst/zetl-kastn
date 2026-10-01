using ZETL;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlHoldCalibrationTests
{
    [Fact] public void SeparatedPressesSuggestAThresholdLeaningTowardTheHolds()
    {
        var result = ZetlHoldCalibration.Analyze(
            tapMs: [80, 95, 110, 120, 180],
            holdMs: [420, 500, 610, 700],
            currentMs: 353);

        AssertTrue(result.Separated, "Every tap ended before every hold.");
        AssertEqual(110.0, result.TapMedianMs, "Median tap.");
        AssertEqual(180.0, result.SlowestTapMs, "Slowest tap.");
        AssertEqual(420.0, result.QuickestHoldMs, "Quickest hold.");
        AssertEqual(350.0, result.SuggestedMs, "70% of the way from 180 to 420.");
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
        AssertEqual(310.0, odd.SuggestedMs, "311.7 rounds to the nearest 5 ms.");
    }

    [Fact] public void TheUsersMeasuredPressesLandInTheRangeThatFeltRight()
    {
        // Two real runs (2026-09-30). The plain midpoint suggested 230 and 235,
        // which felt too fast; 280-290 felt right.
        var first = ZetlHoldCalibration.Analyze(
            [81, 108, 107, 99, 125, 76, 109, 103, 114, 103, 94, 98, 100, 112, 54, 107, 114, 93, 81, 106],
            [433, 352, 339, 355, 353, 363, 403, 398, 415, 414, 347, 365, 400, 418, 376],
            353);
        var second = ZetlHoldCalibration.Analyze(
            [72, 62, 76, 76, 56, 58, 70, 88, 77, 63, 82, 72, 80, 99, 85, 86, 79, 79, 72, 81],
            [370, 413, 429, 452, 515, 511, 563, 580, 607, 579, 588, 597, 619, 565, 566],
            230);

        AssertEqual(275.0, first.SuggestedMs, "First run.");
        AssertEqual(290.0, second.SuggestedMs, "Second run.");
    }

    [Fact] public void SlowerPressesGetALongerThreshold()
    {
        // Someone whose ordinary copy takes up to 320 ms (a motor difficulty, a
        // stiff keyboard) needs protection for their taps, not their holds: a
        // threshold short enough for a fast typist would turn their copies into
        // popups.
        var result = ZetlHoldCalibration.Analyze(
            [150, 210, 260, 300, 320],
            [600, 680, 750, 900],
            353);

        AssertEqual(2, result.CurrentTapsTooLong, "The default 353 would misread their slowest taps.");
        AssertEqual(515.0, result.SuggestedMs, "70% of the way from 320 to 600.");
    }

    [Fact] public void TheSuggestionKeepsAMarginBelowTheQuickestHold()
    {
        // A narrow gap: 70% of the way would sit 9 ms from the quickest hold.
        var result = ZetlHoldCalibration.Analyze([200], [230], 353);
        AssertEqual(215.0, result.SuggestedMs, "Falls back to the middle rather than crowding the hold.");
    }
}
