namespace ZETL;

/// <summary>
/// The platform's global keyboard interception plus synthetic replay. Windows
/// uses a low-level hook and SendInput; a Linux backend will use evdev (grab and
/// read) and uinput. Chordl's processor sits above this on every platform: raw
/// key events flow in, its suppress decision flows back out, and replayed chords
/// flow through <see cref="SendChord"/> / <see cref="SendPaste"/>.
/// </summary>
internal interface IKeyboardBackend : IDisposable
{
    /// <summary>
    /// Install the interception. Every raw key event is passed to
    /// <paramref name="handleKeyEvent"/> as (vkCode, isKeyDown, isKeyUp,
    /// isRepeat); returning true suppresses that event from the foreground app.
    /// <c>isRepeat</c> distinguishes a hardware auto-repeat key-down (a key held
    /// down) from a fresh press, so the processor can suppress leaked repeats
    /// after a hold without blocking a genuine re-press. Returns false if the
    /// interception could not be installed.
    /// </summary>
    bool Start(Func<int, bool, bool, bool, bool> handleKeyEvent);

    /// <summary>
    /// Replay a Ctrl[+Shift]+&lt;key&gt; chord into the foreground app -- used to
    /// pass a tapped shortcut through and by Zetl to send paste. The returned
    /// task completes with the actual injection result (true only once the
    /// platform accepted the synthetic input), so callers that must not act on a
    /// paste that never landed -- e.g. consuming a replay note -- can await it.
    /// Backends may queue the work; the task completes when it actually runs.
    /// </summary>
    Task<bool> SendChord(int vkCode, bool includeShift, bool restoreCtrl, bool restoreShift);

    /// <summary>
    /// Replay a plain Ctrl+V paste into the foreground app. The returned task
    /// completes with the real injection result; see <see cref="SendChord"/>.
    /// </summary>
    Task<bool> SendPaste();
}
