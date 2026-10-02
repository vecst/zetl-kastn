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
        // The quick end of the holds is 468 (the 20th percentile); 70% of the
        // way there from 180 is 381.6.
        AssertEqual(380.0, result.SuggestedMs, "Leans toward the holds.");
        AssertEqual(0, result.SuggestedHoldsTooShort, "Every hold reaches it.");
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
        // Two hold-lab runs (2026-09-30) and two tour runs (2026-10-01). The
        // first model suggested 230-235 for the lab runs and 195 for the tour
        // runs, and anything at 230 or under felt twitchy rather than deliberate.
        var lab1 = ZetlHoldCalibration.Analyze(
            [81, 108, 107, 99, 125, 76, 109, 103, 114, 103, 94, 98, 100, 112, 54, 107, 114, 93, 81, 106],
            [433, 352, 339, 355, 353, 363, 403, 398, 415, 414, 347, 365, 400, 418, 376],
            353);
        var lab2 = ZetlHoldCalibration.Analyze(
            [72, 62, 76, 76, 56, 58, 70, 88, 77, 63, 82, 72, 80, 99, 85, 86, 79, 79, 72, 81],
            [370, 413, 429, 452, 515, 511, 563, 580, 607, 579, 588, 597, 619, 565, 566],
            230);
        var tour1 = ZetlHoldCalibration.Analyze(
            [80, 81, 88, 70, 53, 65, 63, 56, 76, 68],
            [238, 259, 297, 268, 287, 303, 350, 324, 341, 312],
            293);
        var tour2 = ZetlHoldCalibration.Analyze(
            [90, 68, 75, 62, 83, 73, 72, 80, 63, 75],
            [240, 271, 326, 359, 352, 354, 365, 367, 397, 378],
            293);

        AssertEqual(285.0, lab1.SuggestedMs, "First lab run.");
        AssertEqual(345.0, lab2.SuggestedMs, "Second lab run, with longer holds.");
        AssertEqual(240.0, tour1.SuggestedMs, "Short tour holds hold it under the deliberate floor.");
        AssertEqual(250.0, tour2.SuggestedMs, "The deliberate floor.");
    }

    [Fact] public void OneEarlyHoldDoesNotDragTheSuggestionDown()
    {
        var result = ZetlHoldCalibration.Analyze(
            [80, 90, 100],
            [150, 400, 420, 450, 480, 500, 520, 540, 560, 580],
            353);

        AssertTrue(result.Separated, "The 150 ms hold still ends after every tap.");
        AssertEqual(320.0, result.SuggestedMs, "Set by the quick end of the holds, not the one stray.");
        AssertEqual(1, result.SuggestedHoldsTooShort, "Only the stray hold falls short.");
    }

    [Fact] public void QuickHandsStillGetADeliberateHold()
    {
        // 70% of the gap would be 237 ms: safe for these taps, but twitchy.
        var result = ZetlHoldCalibration.Analyze(
            [60, 70, 80],
            [300, 320, 340, 360, 380],
            353);

        AssertEqual(ZetlHoldCalibration.DeliberateFloorMs, result.SuggestedMs, "Raised to the deliberate floor.");
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
            currentMs: 290);

        AssertEqual(2, result.CurrentTapsTooLong, "A fast typist's 290 would turn their two slowest copies into popups.");
        // Twice the slowest tap is 640, past the holds' quick end (648) less the
        // margin, so the suggestion sits at that edge.
        AssertEqual(625.0, result.SuggestedMs, "Slow taps push past the hold-driven maximum.");
    }

    [Fact] public void TheSuggestionKeepsAMarginBelowTheQuickestHold()
    {
        // A narrow gap: 70% of the way would sit 9 ms from the quickest hold.
        var result = ZetlHoldCalibration.Analyze([200], [230], 353);
        AssertEqual(215.0, result.SuggestedMs, "Falls back to the middle rather than crowding the hold.");
    }
}
