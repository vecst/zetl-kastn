namespace Chordl;

public enum ChordlDispatchMode
{
    None,
    Immediate,
    TapOnly
}

public readonly record struct ChordlChord(int KeyCode, bool Ctrl, bool Shift);

public sealed record ChordlAction(
    string Name,
    ChordlDispatchMode Dispatch,
    bool ReplayShift);

public sealed record ChordlEventContext(
    int KeyCode,
    string Name,
    ChordlDispatchMode Dispatch,
    bool ReplayShift,
    bool ShiftLane,
    uint ClipboardSequenceNumber);

public sealed record ChordlConfiguration(
    Dictionary<ChordlChord, ChordlAction> Actions,
    HashSet<int> ConfiguredKeyCodes,
    TimeSpan RepeatSuppressionDelay,
    TimeSpan HoldDelay);

