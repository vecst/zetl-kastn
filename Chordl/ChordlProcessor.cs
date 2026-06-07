using System.Diagnostics;

namespace Chordl;

public sealed class ChordlProcessor : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<ChordlChord, ChordlAction> actions;
    private readonly HashSet<int> configuredKeyCodes;
    private readonly TimeSpan repeatSuppressionDelay;
    private readonly TimeSpan holdDelay;
    private readonly Action<int, bool, bool, bool> dispatchOriginalAction;
    private readonly Action<ChordlEventContext> physicalShortcutPassedThrough;
    private readonly Func<ChordlEventContext, bool> tapDispatched;
    private readonly Action<ChordlEventContext> holdActionDetected;
    private readonly Action<string> logEvent;
    private readonly Func<uint> getClipboardSequenceNumber;
    private readonly TimeSpan postCtrlReleaseSuppression = TimeSpan.FromMilliseconds(400);
    private readonly HashSet<int> pressedShiftKeys = [];

    private bool ctrlDown;
    private bool shiftDown;
    private int activeKeyCode;
    private ChordlAction? activeAction;
    private string activeComboName = "";
    private bool comboCandidateActive;
    private bool holdDetected;
    private long comboStartedAt;
    private int suppressedKeyCodeAfterCtrlRelease;
    private long suppressKeyDownUntil;
    private System.Threading.Timer? holdTimer;

    public ChordlProcessor(
        Dictionary<ChordlChord, ChordlAction> actions,
        HashSet<int> configuredKeyCodes,
        TimeSpan repeatSuppressionDelay,
        TimeSpan holdDelay,
        Action<int, bool, bool, bool> dispatchOriginalAction,
        Action<ChordlEventContext> physicalShortcutPassedThrough,
        Func<ChordlEventContext, bool> tapDispatched,
        Action<ChordlEventContext> holdActionDetected,
        Action<string> logEvent,
        Func<uint> getClipboardSequenceNumber)
    {
        this.actions = actions;
        this.configuredKeyCodes = configuredKeyCodes;
        this.repeatSuppressionDelay = repeatSuppressionDelay;
        this.holdDelay = holdDelay;
        this.dispatchOriginalAction = dispatchOriginalAction;
        this.physicalShortcutPassedThrough = physicalShortcutPassedThrough;
        this.tapDispatched = tapDispatched;
        this.holdActionDetected = holdActionDetected;
        this.logEvent = logEvent;
        this.getClipboardSequenceNumber = getClipboardSequenceNumber;
    }

    public bool HandleKeyEvent(int vkCode, bool isKeyDown, bool isKeyUp)
    {
        if (ChordlKeys.IsControlKey(vkCode))
        {
            lock (gate)
            {
                if (isKeyDown)
                {
                    ctrlDown = true;
                }
                else if (isKeyUp)
                {
                    ctrlDown = false;
                    ArmPostCtrlReleaseSuppression();
                    if (ShouldWaitForTapOnlyKeyUpAfterCtrlRelease())
                    {
                        holdTimer?.Dispose();
                        holdTimer = null;
                        logEvent($"{activeComboName} Ctrl released before target key; waiting for tap key-up.");
                    }
                    else
                    {
                        ResetCombo();
                    }
                }
            }

            return false;
        }

        if (isKeyDown && ShouldSuppressPostCtrlReleaseRepeat(vkCode))
        {
            return true;
        }

        if (isKeyUp && vkCode == suppressedKeyCodeAfterCtrlRelease)
        {
            ClearPostCtrlReleaseSuppression();
        }

        if (ChordlKeys.IsShiftKey(vkCode))
        {
            lock (gate)
            {
                var wasShiftDown = shiftDown;
                if (isKeyDown)
                {
                    pressedShiftKeys.Add(vkCode);
                }
                else if (isKeyUp)
                {
                    pressedShiftKeys.Remove(vkCode);
                }

                shiftDown = pressedShiftKeys.Count > 0;
                if (!comboCandidateActive || shiftDown == wasShiftDown)
                {
                    return false;
                }

                if (holdDetected)
                {
                    logEvent($"{activeComboName} modifier changed after hold; active combo retained.");
                    return false;
                }

                UpdateActiveComboForCurrentModifiers();
                RestartHoldCounter();
                logEvent($"Switched active combo to {activeComboName}; hold counter restarted.");
            }

            return false;
        }

        if (!configuredKeyCodes.Contains(vkCode))
        {
            return false;
        }

        if (isKeyUp)
        {
            lock (gate)
            {
                if (vkCode != activeKeyCode)
                {
                    return false;
                }

                var mode = activeAction?.Dispatch ?? ChordlDispatchMode.None;
                var elapsed = Stopwatch.GetElapsedTime(comboStartedAt);
                var releasedBeforeHold = elapsed < holdDelay;
                var shouldSuppressKeyUp = mode != ChordlDispatchMode.None;
                if (mode == ChordlDispatchMode.TapOnly && !holdDetected && releasedBeforeHold && activeAction is not null)
                {
                    var context = CreateContext();
                    var handled = tapDispatched(context);
                    if (!handled)
                    {
                        DispatchOriginalAction(activeKeyCode, activeAction.ReplayShift);
                        logEvent($"{activeComboName} tapped; original action dispatched.");
                    }
                }

                ResetCombo();
                return shouldSuppressKeyUp;
            }
        }

        if (!isKeyDown)
        {
            return false;
        }

        lock (gate)
        {
            if (!ctrlDown)
            {
                return false;
            }

            var now = Stopwatch.GetTimestamp();

            if (!comboCandidateActive)
            {
                activeKeyCode = vkCode;
                UpdateActiveComboForCurrentModifiers();
                if (activeAction is null)
                {
                    activeKeyCode = 0;
                    return false;
                }

                comboCandidateActive = true;
                holdDetected = false;
                comboStartedAt = now;
                StartHoldTimer();
                if (activeAction.Dispatch == ChordlDispatchMode.Immediate)
                {
                    DispatchOriginalAction(activeKeyCode, activeAction.ReplayShift);
                    logEvent($"{activeComboName} armed; original action dispatched immediately.");
                    physicalShortcutPassedThrough(CreateContext());
                    return true;
                }

                if (activeAction.Dispatch == ChordlDispatchMode.TapOnly)
                {
                    logEvent($"{activeComboName} armed; tap will dispatch original action, hold will not.");
                    return true;
                }

                logEvent($"{activeComboName} first press passed through.");
                physicalShortcutPassedThrough(CreateContext());
                return false;
            }

            if (vkCode != activeKeyCode)
            {
                return false;
            }

            var elapsed = Stopwatch.GetElapsedTime(comboStartedAt, now);

            if ((activeAction?.Dispatch ?? ChordlDispatchMode.None) != ChordlDispatchMode.None)
            {
                return true;
            }

            if (elapsed >= repeatSuppressionDelay)
            {
                return true;
            }

            logEvent($"Early repeat passed through at {elapsed.TotalMilliseconds:0}ms.");
            return false;
        }
    }

    public void Dispose()
    {
        holdTimer?.Dispose();
    }

    private void DispatchOriginalAction(int vkCode, bool includeShift)
    {
        dispatchOriginalAction(vkCode, includeShift, ctrlDown, shiftDown);
    }

    private void StartHoldTimer()
    {
        holdTimer?.Dispose();
        holdTimer = new System.Threading.Timer(_ =>
        {
            ChordlEventContext? context = null;
            lock (gate)
            {
                if (!comboCandidateActive || holdDetected || !ctrlDown || activeKeyCode == 0)
                {
                    return;
                }

                holdDetected = true;
                context = CreateContext();
                logEvent($"Hold detected for {activeComboName}.");
            }

            if (context is not null)
            {
                holdActionDetected(context);
            }
        }, null, holdDelay, Timeout.InfiniteTimeSpan);
    }

    private void RestartHoldCounter()
    {
        comboStartedAt = Stopwatch.GetTimestamp();
        holdDetected = false;
        StartHoldTimer();
    }

    private void ArmPostCtrlReleaseSuppression()
    {
        if (!comboCandidateActive || activeKeyCode == 0)
        {
            return;
        }

        suppressedKeyCodeAfterCtrlRelease = activeKeyCode;
        suppressKeyDownUntil = Stopwatch.GetTimestamp() + (long)(postCtrlReleaseSuppression.TotalSeconds * Stopwatch.Frequency);
    }

    private bool ShouldSuppressPostCtrlReleaseRepeat(int vkCode)
    {
        lock (gate)
        {
            if (vkCode != suppressedKeyCodeAfterCtrlRelease)
            {
                return false;
            }

            if (Stopwatch.GetTimestamp() > suppressKeyDownUntil)
            {
                ClearPostCtrlReleaseSuppression();
                return false;
            }

            return true;
        }
    }

    private void ClearPostCtrlReleaseSuppression()
    {
        suppressedKeyCodeAfterCtrlRelease = 0;
        suppressKeyDownUntil = 0;
    }

    private bool ShouldWaitForTapOnlyKeyUpAfterCtrlRelease()
    {
        if (!comboCandidateActive || activeAction?.Dispatch != ChordlDispatchMode.TapOnly)
        {
            return false;
        }

        if (holdDetected)
        {
            return true;
        }

        return Stopwatch.GetElapsedTime(comboStartedAt) < holdDelay;
    }

    private void UpdateActiveComboForCurrentModifiers()
    {
        actions.TryGetValue(new ChordlChord(activeKeyCode, Ctrl: true, Shift: shiftDown), out activeAction);
        activeComboName = activeAction?.Name ?? ChordlKeys.FormatComboName(activeKeyCode, shiftDown);
    }

    private ChordlEventContext CreateContext()
    {
        return new ChordlEventContext(
            activeKeyCode,
            activeComboName,
            activeAction?.Dispatch ?? ChordlDispatchMode.None,
            activeAction?.ReplayShift ?? false,
            shiftDown,
            getClipboardSequenceNumber());
    }

    private void ResetCombo()
    {
        if (comboCandidateActive)
        {
            logEvent($"{activeComboName} combo ended.");
        }

        activeKeyCode = 0;
        activeAction = null;
        activeComboName = "";
        comboCandidateActive = false;
        holdDetected = false;
        comboStartedAt = 0;
        holdTimer?.Dispose();
        holdTimer = null;
    }
}

