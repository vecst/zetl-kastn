using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Tests.ZetlTestSupport;
using Xunit;
using System;
using System.Collections.Generic;

namespace ZETL.Tests;

[Collection(RealTimeCollection.Name)]
public class ChordlKeyboardTests
{
    [Fact(DisplayName = "Ctrl+C pass-through suppresses later repeats")]
    public static void CopyPassThroughSuppressesRepeats()
    {
        using var processor = CreateProcessor(out _, out _, out _, out _);
        AssertFalse(processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false), "Ctrl down should pass through.");
        AssertFalse(processor.HandleKeyEvent(VK_C, isKeyDown: true, isKeyUp: false), "First copy press should pass through.");
        Thread.Sleep(30);
        AssertTrue(processor.HandleKeyEvent(VK_C, isKeyDown: true, isKeyUp: false), "Later repeat should suppress.");
        processor.HandleKeyEvent(VK_C, isKeyDown: false, isKeyUp: true);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
    }

    [Fact(DisplayName = "Ctrl+V tap dispatches paste on key-up")]
    public static void PasteTapDispatchesOnKeyUp()
    {
        using var processor = CreateProcessor(out var dispatched, out _, out var taps, out _);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false), "Paste key down should suppress.");
        AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true), "Paste key up should suppress physical event.");
        AssertEqual(1, dispatched.Count, "Tap should dispatch one synthetic paste.");
        AssertEqual(VK_V, dispatched[0], "Dispatched key should be V.");
        AssertEqual(1, taps.Count, "Tap callback should fire once.");
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
    }

    [Fact(DisplayName = "Ctrl+V gallop tap dispatches after Ctrl key-up")]
    public static void PasteGallopTapDispatchesAfterCtrlKeyUp()
    {
        using var processor = CreateProcessor(out var dispatched, out _, out var taps, out _);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false), "Paste key down should suppress.");
        AssertFalse(processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true), "Ctrl key up should pass through.");
        AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true), "Paste key up should still finish the tap.");
        AssertEqual(1, dispatched.Count, "Gallop tap should dispatch one synthetic paste.");
        AssertEqual(VK_V, dispatched[0], "Dispatched key should be V.");
        AssertEqual(1, taps.Count, "Tap callback should fire once.");
    }

    [Fact(DisplayName = "Ctrl+V late gallop does not dispatch paste")]
    public static void PasteLateGallopDoesNotDispatch()
    {
        using var processor = CreateProcessor(out var dispatched, out _, out _, out _);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false), "Paste key down should suppress.");
        Thread.Sleep(80);
        AssertFalse(processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true), "Ctrl key up should pass through.");
        _ = processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true);
        AssertEqual(0, dispatched.Count, "Late gallop should not dispatch paste.");
    }

    [Fact(DisplayName = "Ctrl+V handled tap suppresses default paste")]
    public static void PasteHandledTapSuppressesDefaultPaste()
    {
        using var processor = CreateProcessor(
            out var dispatched,
            out _,
            out var taps,
            out _,
            tapHandled: true);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false), "Paste key down should suppress.");
        AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true), "Paste key up should suppress physical event.");
        AssertEqual(1, taps.Count, "Handled tap callback should fire once.");
        AssertEqual(0, dispatched.Count, "Handled tap should not dispatch the default paste.");
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
    }

    [Fact(DisplayName = "Ctrl+V hold reserves paste")]
    public static void PasteHoldDoesNotDispatch()
    {
        using var processor = CreateProcessor(out var dispatched, out _, out _, out var holds);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false), "Paste key down should suppress.");
        WaitForHold(holds, "Hold callback should fire once.");
        AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true), "Held paste key up should suppress.");
        AssertEqual(0, dispatched.Count, "Held paste should not dispatch paste.");
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
    }

    [Fact(DisplayName = "Ctrl+V hold suppresses repeats after Ctrl key-up")]
    public static void PasteHoldSuppressesRepeatsAfterCtrlKeyUp()
    {
        using var processor = CreateProcessor(out var dispatched, out _, out _, out var holds);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false), "Paste key down should suppress.");
        WaitForHold(holds, "Hold callback should fire once.");
        AssertFalse(processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true), "Ctrl key up should pass through.");
        AssertTrue(
            processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false, isRepeat: true),
            "Post-Ctrl target repeat should suppress.");
        AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true), "Held paste key up should suppress.");
        AssertEqual(0, dispatched.Count, "Held paste should not dispatch paste.");
    }

    [Fact(DisplayName = "Fresh target key passes after Ctrl-up repeat guard")]
    public static void FreshTargetKeyPassesAfterCtrlUpRepeatGuard()
    {
        using var processor = CreateProcessor(out var dispatched, out _, out _, out var holds);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false), "Paste key down should suppress.");
        WaitForHold(holds, "Hold callback should fire once.");
        AssertFalse(processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true), "Ctrl key up should pass through.");
        AssertTrue(
            processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false, isRepeat: true),
            "Post-Ctrl target repeat should suppress.");
        AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true), "Held paste key up should suppress.");
        AssertFalse(
            processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false, isRepeat: false),
            "Fresh target key press after the original key-up should pass through.");
        AssertFalse(processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true), "Fresh target key-up should pass through.");
        AssertEqual(0, dispatched.Count, "Held paste should not dispatch paste.");
    }

    [Fact(DisplayName = "Ctrl+B tap dispatches board shortcut on key-up")]
    public static void BoardTapDispatchesOnKeyUp()
    {
        using var processor = CreateProcessor(out var dispatched, out _, out var taps, out _);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        AssertTrue(processor.HandleKeyEvent(VK_B, isKeyDown: true, isKeyUp: false), "Board key down should suppress.");
        AssertTrue(processor.HandleKeyEvent(VK_B, isKeyDown: false, isKeyUp: true), "Board key up should suppress physical event.");
        AssertEqual(1, dispatched.Count, "Tap should dispatch one synthetic board shortcut.");
        AssertEqual(VK_B, dispatched[0], "Dispatched key should be B.");
        AssertEqual(1, taps.Count, "Tap callback should fire once.");
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
    }

    [Fact(DisplayName = "Ctrl+B hold opens board without dispatch")]
    public static void BoardHoldDoesNotDispatch()
    {
        using var processor = CreateProcessor(out var dispatched, out _, out _, out var holds);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        AssertTrue(processor.HandleKeyEvent(VK_B, isKeyDown: true, isKeyUp: false), "Board key down should suppress.");
        WaitForHold(holds, "Hold callback should fire once.");
        AssertEqual("Ctrl+B", holds[0].Name, "Hold should use board chord.");
        AssertTrue(processor.HandleKeyEvent(VK_B, isKeyDown: false, isKeyUp: true), "Held board key up should suppress.");
        AssertEqual(0, dispatched.Count, "Held board chord should not dispatch Ctrl+B.");
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
    }

    [Fact(DisplayName = "Ctrl+P tap dispatches the P key on key-up")]
    public static void PassThroughToggleTapDispatchesOnKeyUp()
    {
        using var processor = CreateProcessor(out var dispatched, out _, out var taps, out _);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        AssertTrue(processor.HandleKeyEvent(VK_P, isKeyDown: true, isKeyUp: false), "Pass-through toggle key down should suppress.");
        AssertTrue(processor.HandleKeyEvent(VK_P, isKeyDown: false, isKeyUp: true), "Pass-through toggle key up should suppress physical event.");
        AssertEqual(1, dispatched.Count, "Tap should dispatch one synthetic shortcut.");
        AssertEqual(VK_P, dispatched[0], "Dispatched key should be P.");
        AssertEqual(1, taps.Count, "Tap callback should fire once.");
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
    }

    [Fact(DisplayName = "Ctrl+P hold raises the pass-through toggle")]
    public static void PassThroughToggleHoldDoesNotDispatch()
    {
        using var processor = CreateProcessor(out var dispatched, out _, out _, out var holds);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        AssertTrue(processor.HandleKeyEvent(VK_P, isKeyDown: true, isKeyUp: false), "Pass-through toggle key down should suppress.");
        WaitForHold(holds, "Hold callback should fire once.");
        AssertEqual("Ctrl+P", holds[0].Name, "Hold should use pass-through toggle chord.");
        AssertTrue(processor.HandleKeyEvent(VK_P, isKeyDown: false, isKeyUp: true), "Held pass-through toggle key up should suppress.");
        AssertEqual(0, dispatched.Count, "Held pass-through toggle should not dispatch the P key.");
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
    }

    [Fact(DisplayName = "Ctrl+R tap dispatches replay key on key-up")]
    public static void ReplayToggleTapDispatchesOnKeyUp()
    {
        using var processor = CreateProcessor(out var dispatched, out _, out var taps, out _);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        AssertTrue(processor.HandleKeyEvent(VK_R, isKeyDown: true, isKeyUp: false), "Replay toggle key down should suppress.");
        AssertTrue(processor.HandleKeyEvent(VK_R, isKeyDown: false, isKeyUp: true), "Replay toggle key up should suppress physical event.");
        AssertEqual(1, dispatched.Count, "Tap should dispatch one synthetic shortcut.");
        AssertEqual(VK_R, dispatched[0], "Dispatched key should be R.");
        AssertEqual(1, taps.Count, "Tap callback should fire once.");
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
    }

    [Fact(DisplayName = "Ctrl+R hold raises Replay toggle")]
    public static void ReplayToggleHoldDoesNotDispatch()
    {
        using var processor = CreateProcessor(out var dispatched, out _, out _, out var holds);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        AssertTrue(processor.HandleKeyEvent(VK_R, isKeyDown: true, isKeyUp: false), "Replay toggle key down should suppress.");
        WaitForHold(holds, "Hold callback should fire once.");
        AssertEqual("Ctrl+R", holds[0].Name, "Hold should use Replay toggle chord.");
        AssertTrue(processor.HandleKeyEvent(VK_R, isKeyDown: false, isKeyUp: true), "Held Replay toggle key up should suppress.");
        AssertEqual(0, dispatched.Count, "Held Replay toggle should not dispatch the replay key.");
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
    }

    [Fact(DisplayName = "Ctrl+Z hold raises Zetl undo")]
    public static void UndoHoldDoesNotDispatch()
    {
        using var processor = CreateProcessor(out var dispatched, out _, out _, out var holds);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        AssertTrue(processor.HandleKeyEvent(VK_Z, isKeyDown: true, isKeyUp: false), "Undo key down should suppress.");
        WaitForHold(holds, "Hold callback should fire once.");
        AssertEqual("Ctrl+Z", holds[0].Name, "Hold should use undo chord.");
        AssertTrue(processor.HandleKeyEvent(VK_Z, isKeyDown: false, isKeyUp: true), "Held undo key up should suppress.");
        AssertEqual(0, dispatched.Count, "Held undo should not dispatch app undo.");
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
    }

    [Fact(DisplayName = "Shift changes restart hold detection")]
    public static void ShiftChangeRestartsHold()
    {
        using var processor = CreateProcessor(out _, out _, out _, out var holds);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        processor.HandleKeyEvent(VK_C, isKeyDown: true, isKeyUp: false);
        Thread.Sleep(25);
        processor.HandleKeyEvent(VK_SHIFT, isKeyDown: true, isKeyUp: false);
        Thread.Sleep(25);
        AssertEqual(0, holds.Count, "Hold should not fire before the restarted threshold.");
        WaitForHold(holds, "Hold should fire after Shift restart threshold.");
        AssertEqual("Ctrl+Shift+C", holds[0].Name, "Hold should use shifted chord.");
        processor.HandleKeyEvent(VK_C, isKeyDown: false, isKeyUp: true);
        processor.HandleKeyEvent(VK_SHIFT, isKeyDown: false, isKeyUp: true);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
    }

    [Fact(DisplayName = "Shift repeat does not restart hold detection")]
    public static void ShiftRepeatDoesNotRestartHold()
    {
        using var processor = CreateProcessor(out _, out _, out _, out var holds);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        processor.HandleKeyEvent(VK_C, isKeyDown: true, isKeyUp: false);
        Thread.Sleep(20);
        processor.HandleKeyEvent(VK_SHIFT, isKeyDown: true, isKeyUp: false);
        Thread.Sleep(45);
        processor.HandleKeyEvent(VK_SHIFT, isKeyDown: true, isKeyUp: false);
        WaitForHold(holds, "Shift autorepeat should not postpone the restarted hold.");
        AssertEqual("Ctrl+Shift+C", holds[0].Name, "Hold should retain the shifted chord.");
        processor.HandleKeyEvent(VK_C, isKeyDown: false, isKeyUp: true);
        processor.HandleKeyEvent(VK_SHIFT, isKeyDown: false, isKeyUp: true);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
    }

    [Fact(DisplayName = "Shift change after hold does not dispatch twice")]
    public static void ShiftChangeAfterHoldDoesNotDispatchTwice()
    {
        using var processor = CreateProcessor(
            out var dispatched,
            out _,
            out var taps,
            out var holds);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
        processor.HandleKeyEvent(VK_B, isKeyDown: true, isKeyUp: false);
        WaitForHold(holds, "Initial hold should fire once.");
        processor.HandleKeyEvent(VK_SHIFT, isKeyDown: true, isKeyUp: false);
        processor.HandleKeyEvent(VK_SHIFT, isKeyDown: true, isKeyUp: false);
        Thread.Sleep(90);
        processor.HandleKeyEvent(VK_B, isKeyDown: false, isKeyUp: true);
        AssertEqual(1, holds.Count, "Modifier changes after a hold must not fire another hold.");
        AssertEqual(0, taps.Count, "Modifier changes after a hold must not turn it into a tap.");
        AssertEqual(0, dispatched.Count, "Modifier changes after a hold must not replay the shortcut.");
        processor.HandleKeyEvent(VK_SHIFT, isKeyDown: false, isKeyUp: true);
        processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
    }

    [Fact(DisplayName = "Synthetic modifier injection uses an unheld side")]
    public static void SyntheticModifierUsesUnheldSide()
    {
        AssertEqual<int?>(
            VK_LCONTROL,
            ZetlSyntheticModifier.SelectInjection(
                leftDown: false,
                rightDown: false,
                VK_LCONTROL,
                VK_RCONTROL),
            "With neither side held, injection should use left Ctrl.");
        AssertEqual<int?>(
            null,
            ZetlSyntheticModifier.SelectInjection(
                leftDown: true,
                rightDown: false,
                VK_LCONTROL,
                VK_RCONTROL),
            "With left held, no synthetic modifier is needed.");
        AssertEqual<int?>(
            null,
            ZetlSyntheticModifier.SelectInjection(
                leftDown: false,
                rightDown: true,
                VK_LCONTROL,
                VK_RCONTROL),
            "With right held, no synthetic modifier is needed.");
        AssertEqual<int?>(
            null,
            ZetlSyntheticModifier.SelectInjection(
                leftDown: true,
                rightDown: true,
                VK_LCONTROL,
                VK_RCONTROL),
            "With both sides held, no synthetic modifier is needed.");
    }

    [Fact(DisplayName = "Chord injection suppresses a held Shift for a plain chord")]
    public static void ChordInjectionSuppressesHeldShiftForPlainChord()
    {
        // Shift-lane copy/cut replays a plain Ctrl+C/Ctrl+X while the user
        // physically holds Ctrl+Shift. A held Shift must be released for the
        // chord (otherwise the app sees Ctrl+Shift+key, not a copy/cut) and
        // restored afterwards.
        var sequence = ZetlChordInjection.BuildCtrlChord(
            VK_C,
            includeShift: false,
            leftCtrlDown: true,
            rightCtrlDown: false,
            leftShiftDown: true,
            rightShiftDown: false);

        var keyDownIndex = -1;
        var keyUpAfterDownIndex = -1;
        for (var i = 0; i < sequence.Count; i++)
        {
            if (sequence[i].VirtualKey == VK_C && !sequence[i].KeyUp)
            {
                keyDownIndex = i;
            }
            else if (sequence[i].VirtualKey == VK_C
                && sequence[i].KeyUp
                && keyDownIndex >= 0
                && keyUpAfterDownIndex < 0)
            {
                keyUpAfterDownIndex = i;
            }
        }

        AssertTrue(keyDownIndex >= 0, "The chord must press the target key.");
        AssertTrue(keyUpAfterDownIndex > keyDownIndex, "The chord must release the target key.");

        var shiftDownDuringPress = false;
        for (var i = 0; i < keyDownIndex; i++)
        {
            if (IsSidedShiftKey(sequence[i].VirtualKey))
            {
                shiftDownDuringPress = !sequence[i].KeyUp;
            }
        }

        AssertTrue(
            !shiftDownDuringPress,
            "A held Shift must be released before a plain Ctrl chord so it is not Ctrl+Shift+key.");

        var shiftRestoredAfter = false;
        for (var i = keyUpAfterDownIndex + 1; i < sequence.Count; i++)
        {
            if (IsSidedShiftKey(sequence[i].VirtualKey) && !sequence[i].KeyUp)
            {
                shiftRestoredAfter = true;
            }
        }

        AssertTrue(shiftRestoredAfter, "A suppressed Shift must be restored after the chord.");

        // Sanity: a shifted chord with Shift already held keeps it down.
        var shifted = ZetlChordInjection.BuildCtrlChord(
            VK_C,
            includeShift: true,
            leftCtrlDown: true,
            rightCtrlDown: true,
            leftShiftDown: true,
            rightShiftDown: true);
        foreach (var keyEvent in shifted)
        {
            AssertTrue(
                !(IsSidedShiftKey(keyEvent.VirtualKey) && keyEvent.KeyUp),
                "A shifted chord with both Shifts held must not release Shift.");
        }
    }
}
