using Chordl;

namespace ZETL;

/// <summary>One synthetic key event: a virtual-key press or release.</summary>
public readonly record struct ChordKeyEvent(int VirtualKey, bool KeyUp);

/// <summary>
/// Builds the ordered key-event sequence for replaying a Ctrl[+Shift]+key chord
/// via synthetic input, given the modifier keys the user is physically holding.
/// Shared by the Windows backends so the modifier handling is identical and
/// testable without SendInput.
/// </summary>
public static class ZetlChordInjection
{
    public static IReadOnlyList<ChordKeyEvent> BuildCtrlChord(
        int vkCode,
        bool includeShift,
        bool leftCtrlDown,
        bool rightCtrlDown,
        bool leftShiftDown,
        bool rightShiftDown)
    {
        var events = new List<ChordKeyEvent>
        {
            // Release the target key first in case it is still physically held
            // (e.g. replaying on key-up of a tapped chord).
            new(vkCode, KeyUp: true),
        };

        // Ctrl is always part of the chord; ensure it is down for the press.
        var ctrlInject = ZetlSyntheticModifier.SelectInjection(
            leftCtrlDown,
            rightCtrlDown,
            ChordlKeys.VK_LCONTROL,
            ChordlKeys.VK_RCONTROL);
        if (ctrlInject is { } ctrlKey)
        {
            events.Add(new ChordKeyEvent(ctrlKey, KeyUp: false));
        }

        int? shiftInject = null;
        var suppressedShift = new List<int>();
        if (includeShift)
        {
            // Want Shift in the chord: ensure it is down for the press.
            shiftInject = ZetlSyntheticModifier.SelectInjection(
                leftShiftDown,
                rightShiftDown,
                ChordlKeys.VK_LSHIFT,
                ChordlKeys.VK_RSHIFT);
            if (shiftInject is { } shiftKey)
            {
                events.Add(new ChordKeyEvent(shiftKey, KeyUp: false));
            }
        }
        else
        {
            // Plain Ctrl chord (e.g. replaying a copy/cut for the Shift lane):
            // a physically-held Shift would make the target app see
            // Ctrl+Shift+key. Release the held Shift for the chord and restore
            // it afterwards so the user's keys are not left in a wrong state.
            if (leftShiftDown)
            {
                suppressedShift.Add(ChordlKeys.VK_LSHIFT);
            }

            if (rightShiftDown)
            {
                suppressedShift.Add(ChordlKeys.VK_RSHIFT);
            }

            foreach (var shift in suppressedShift)
            {
                events.Add(new ChordKeyEvent(shift, KeyUp: true));
            }
        }

        events.Add(new ChordKeyEvent(vkCode, KeyUp: false));
        events.Add(new ChordKeyEvent(vkCode, KeyUp: true));

        if (includeShift)
        {
            if (shiftInject is { } shiftKey)
            {
                events.Add(new ChordKeyEvent(shiftKey, KeyUp: true));
            }
        }
        else
        {
            for (var i = suppressedShift.Count - 1; i >= 0; i--)
            {
                events.Add(new ChordKeyEvent(suppressedShift[i], KeyUp: false));
            }
        }

        if (ctrlInject is { } releaseCtrlKey)
        {
            events.Add(new ChordKeyEvent(releaseCtrlKey, KeyUp: true));
        }

        return events;
    }
}
