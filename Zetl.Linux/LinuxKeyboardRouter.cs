using Chordl;

namespace ZETL;

/// <summary>Where the router writes: Zetl's virtual keyboard, or a test double.</summary>
internal interface ILinuxKeyboardOutput
{
    void Write(LinuxInputEvent inputEvent);
}

/// <summary>
/// The device-free half of the Linux keyboard backend. Grabbed keyboards hand
/// every event here; each key event is offered to Chordl (as on Windows, where
/// the hook asks the same question) and forwarded to the virtual keyboard
/// unless Chordl suppresses it. Replayed chords go out through the same
/// device, so what the desktop sees is always one consistent keyboard.
///
/// Physical events arrive on the backend's reader thread only. Chords may be
/// sent from any thread; the output lock keeps them whole and ordered against
/// forwarded events. Chordl is never called while that lock is held, because
/// Chordl itself may send a chord from inside its handler.
/// </summary>
internal sealed class LinuxKeyboardRouter(
    ILinuxKeyboardOutput output,
    Func<int, bool, bool, bool, bool> handleKeyEvent,
    Action<string> log)
{
    private readonly object outputGate = new();
    private readonly HashSet<ushort> outputDown = [];
    private readonly Dictionary<int, HashSet<ushort>> sourceDown = [];
    private bool frameHasEvents;
    private bool closed;
    private bool leftShiftDown;
    private bool rightShiftDown;

    /// <summary>
    /// Raised on the reader thread once both Shift keys and Escape were pressed:
    /// the escape hatch that works even if Chordl's state is wrong. Every
    /// forwarded key has already been released.
    /// </summary>
    public event Action? PanicRequested;

    public bool Released { get; private set; }

    /// <summary>Keys the virtual keyboard currently holds down.</summary>
    public IReadOnlyCollection<ushort> OutputDown
    {
        get
        {
            lock (outputGate) return outputDown.ToArray();
        }
    }

    public void AddSource(int source) => sourceDown[source] = [];

    public void Route(int source, LinuxInputEvent inputEvent)
    {
        if (Released) return;
        if (inputEvent.IsReport)
        {
            // Close the frame only if something in it was forwarded; a frame whose
            // keys were all suppressed would otherwise reach the desktop empty.
            if (frameHasEvents) Emit(inputEvent);
            return;
        }

        if (!inputEvent.IsKey) return;
        var held = sourceDown.TryGetValue(source, out var keys) ? keys : sourceDown[source] = [];
        if (inputEvent.Value == 0) held.Remove(inputEvent.Code);
        else held.Add(inputEvent.Code);

        if (IsPanic(inputEvent))
        {
            Panic();
            return;
        }

        if (!ShouldSuppress(inputEvent))
        {
            Emit(inputEvent);
        }
        else if (inputEvent.Value == 2 && IsOutputDown(inputEvent.Code))
        {
            // Chordl swallows repeats after a key it let through (a held Ctrl+C
            // copies once). Windows repeats are input events the hook can drop;
            // a Wayland compositor repeats a held key itself and ignores evdev's
            // repeats, so release the key to stop the desktop's own repeat.
            Emit(LinuxInputEvent.Key(inputEvent.Code, 0));
        }
    }

    private bool IsOutputDown(ushort key)
    {
        lock (outputGate) return outputDown.Contains(key);
    }

    /// <summary>
    /// A keyboard vanished. Its held keys are released through the normal path
    /// so Chordl sees each key-up and the desktop is not left holding them.
    /// </summary>
    public void RemoveSource(int source)
    {
        if (!sourceDown.TryGetValue(source, out var held)) return;
        foreach (var key in held.Order().ToArray()) Route(source, LinuxInputEvent.Key(key, 0));
        Route(source, LinuxInputEvent.Synchronize());
        sourceDown.Remove(source);
    }

    /// <summary>
    /// The kernel dropped events (SYN_DROPPED). Reconcile with the keys it now
    /// reports held: synthesize the releases and presses that were lost.
    /// </summary>
    public void Resync(int source, IReadOnlySet<ushort> pressedNow)
    {
        var held = sourceDown.TryGetValue(source, out var keys) ? keys.ToArray() : [];
        foreach (var key in held.Where(key => !pressedNow.Contains(key)).Order())
        {
            Route(source, LinuxInputEvent.Key(key, 0));
        }

        foreach (var key in pressedNow.Where(key => !held.Contains(key)).Order())
        {
            Route(source, LinuxInputEvent.Key(key, 1));
        }

        Route(source, LinuxInputEvent.Synchronize());
    }

    /// <summary>
    /// Replays Ctrl[+Shift]+key through the virtual keyboard. Which modifiers to
    /// add or lift comes from what the virtual keyboard holds right now: that is
    /// exactly the modifier state the desktop sees.
    /// </summary>
    public bool SendChord(int virtualKey, bool includeShift)
    {
        if (!LinuxKeyMap.TryGetLinuxKey(virtualKey, out _)) return false;
        try
        {
            lock (outputGate)
            {
                if (Released) return false;
                var sequence = ZetlChordInjection.BuildCtrlChord(
                    virtualKey,
                    includeShift,
                    outputDown.Contains(LinuxKeyMap.KeyLeftCtrl),
                    outputDown.Contains(LinuxKeyMap.KeyRightCtrl),
                    outputDown.Contains(LinuxKeyMap.KeyLeftShift),
                    outputDown.Contains(LinuxKeyMap.KeyRightShift));
                foreach (var step in sequence)
                {
                    if (!LinuxKeyMap.TryGetLinuxKey(step.VirtualKey, out var key)) continue;
                    // A release for a key the device does not hold changes nothing
                    // (the kernel would drop it), so skip it and its empty frame.
                    if (step.KeyUp && !outputDown.Contains(key)) continue;
                    EmitLocked(LinuxInputEvent.Key(key, step.KeyUp ? 0 : 1));
                    EmitLocked(LinuxInputEvent.Synchronize());
                }
            }

            return true;
        }
        catch (IOException ex)
        {
            log($"Linux keyboard: chord replay failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Releases every key the virtual keyboard holds. Safe to repeat.</summary>
    public void ReleaseAll()
    {
        lock (outputGate)
        {
            foreach (var key in outputDown.Order().ToArray())
            {
                try
                {
                    EmitLocked(LinuxInputEvent.Key(key, 0));
                    EmitLocked(LinuxInputEvent.Synchronize());
                }
                catch (IOException)
                {
                    // The device is gone; destroying it releases the key anyway.
                }
            }

            outputDown.Clear();
        }
    }

    private bool ShouldSuppress(LinuxInputEvent inputEvent)
    {
        if (!LinuxKeyMap.TryGetVirtualKey(inputEvent.Code, out var virtualKey)) return false;
        try
        {
            return handleKeyEvent(
                virtualKey,
                inputEvent.Value is 1 or 2,
                inputEvent.Value == 0,
                inputEvent.Value == 2);
        }
        catch (Exception ex)
        {
            // Fail open: a broken handler must never swallow the user's typing.
            log($"Linux keyboard: key handler failed open: {ex.Message}");
            return false;
        }
    }

    private bool IsPanic(LinuxInputEvent inputEvent)
    {
        if (inputEvent.Code == LinuxKeyMap.KeyLeftShift) leftShiftDown = inputEvent.Value != 0;
        else if (inputEvent.Code == LinuxKeyMap.KeyRightShift) rightShiftDown = inputEvent.Value != 0;
        return inputEvent.Code == LinuxKeyMap.KeyEsc
            && inputEvent.Value == 1
            && leftShiftDown
            && rightShiftDown;
    }

    private void Panic()
    {
        log("Linux keyboard: panic chord (both Shifts + Esc) released the keyboard.");
        lock (outputGate) Released = true;
        ReleaseAll();
        PanicRequested?.Invoke();
    }

    /// <summary>
    /// Stops all output for good, before the virtual keyboard's descriptor is
    /// closed and its number possibly reused. Release keys first.
    /// </summary>
    public void Close()
    {
        lock (outputGate)
        {
            closed = true;
            Released = true;
        }
    }

    private void Emit(LinuxInputEvent inputEvent)
    {
        lock (outputGate) EmitLocked(inputEvent);
    }

    private void EmitLocked(LinuxInputEvent inputEvent)
    {
        if (closed) return;
        output.Write(inputEvent);
        if (inputEvent.IsReport)
        {
            frameHasEvents = false;
            return;
        }

        frameHasEvents = true;
        if (!inputEvent.IsKey) return;
        if (inputEvent.Value == 0) outputDown.Remove(inputEvent.Code);
        else outputDown.Add(inputEvent.Code);
    }
}
