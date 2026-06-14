using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Chordl;

namespace ZETL;

// Windows synthetic input: replays a Ctrl[+Shift]+<key> chord via SendInput.
// Lives with the Windows backend rather than the portable Chordl library; a
// Linux backend sends through uinput instead.
internal static class ChordlInput
{
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private static readonly BlockingCollection<Action> ReplayQueue = [];

    static ChordlInput()
    {
        var thread = new Thread(ProcessReplayQueue)
        {
            IsBackground = true,
            Name = "Zetl Windows synthetic input"
        };
        thread.Start();
    }

    // Returns a task that completes with the real SendInput result once the queued
    // work runs, so callers can await actual injection success rather than mere
    // enqueueing.
    public static Task<bool> SendCtrlChord(int vkCode, bool includeShift, bool restoreCtrl, bool restoreShift, Action<string>? log = null)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            ReplayQueue.Add(() =>
            {
                bool sent;
                try
                {
                    sent = SendCtrlChordNow(vkCode, includeShift, log);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Warning: synthetic Chordl input failed: {ex.Message}.");
                    sent = false;
                }

                completion.TrySetResult(sent);
            });
        }
        catch (InvalidOperationException ex)
        {
            log?.Invoke($"Warning: synthetic Chordl input could not be queued: {ex.Message}.");
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    public static Task<bool> SendPaste(Action<string>? log = null)
    {
        return SendCtrlChord(ChordlKeys.VK_V, includeShift: false, restoreCtrl: false, restoreShift: false, log);
    }

    private static void ProcessReplayQueue()
    {
        foreach (var action in ReplayQueue.GetConsumingEnumerable())
        {
            action();
        }
    }

    private static bool SendCtrlChordNow(int vkCode, bool includeShift, Action<string>? log)
    {
        var modifiers = ModifierSnapshot.Capture();
        var sequence = ZetlChordInjection.BuildCtrlChord(
            vkCode,
            includeShift,
            modifiers.LeftCtrl,
            modifiers.RightCtrl,
            modifiers.LeftShift,
            modifiers.RightShift);
        var inputs = new INPUT[sequence.Count];
        for (var i = 0; i < sequence.Count; i++)
        {
            inputs[i] = KeyInput(sequence[i].VirtualKey, sequence[i].KeyUp);
        }

        Marshal.SetLastPInvokeError(0);
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != (uint)inputs.Length)
        {
            log?.Invoke($"Warning: synthetic Chordl input sent {sent}/{inputs.Length} events. Error: {Marshal.GetLastPInvokeError()}.");
            return false;
        }

        return true;
    }

    private static INPUT KeyInput(int vkCode, bool keyUp)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            union = new InputUnion
            {
                keyboard = new KEYBDINPUT
                {
                    wVk = (ushort)vkCode,
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0
                }
            }
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private readonly record struct ModifierSnapshot(
        bool LeftCtrl,
        bool RightCtrl,
        bool LeftShift,
        bool RightShift)
    {
        public static ModifierSnapshot Capture()
        {
            return new ModifierSnapshot(
                IsDown(ChordlKeys.VK_LCONTROL),
                IsDown(ChordlKeys.VK_RCONTROL),
                IsDown(ChordlKeys.VK_LSHIFT),
                IsDown(ChordlKeys.VK_RSHIFT));
        }

        private static bool IsDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion union;
    }

    // Size = 32 matches the Windows INPUT union on x64. SendInput's cbSize check
    // requires the managed INPUT struct size to match.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }
}

