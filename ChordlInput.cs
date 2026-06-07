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

    public static bool SendCtrlChord(int vkCode, bool includeShift, bool restoreCtrl, bool restoreShift, Action<string>? log = null)
    {
        try
        {
            ReplayQueue.Add(() =>
            {
                try
                {
                    SendCtrlChordNow(vkCode, includeShift, log);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Warning: synthetic Chordl input failed: {ex.Message}.");
                }
            });
            return true;
        }
        catch (InvalidOperationException ex)
        {
            log?.Invoke($"Warning: synthetic Chordl input could not be queued: {ex.Message}.");
            return false;
        }
    }

    public static bool SendPaste(Action<string>? log = null)
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
        var injectCtrl = !modifiers.AnyCtrlDown;
        var injectShift = includeShift && !modifiers.AnyShiftDown;
        var inputCount =
            3
            + (injectCtrl ? 2 : 0)
            + (injectShift ? 2 : 0);

        var inputs = new INPUT[inputCount];
        var index = 0;
        AddKeyInput(vkCode, keyUp: true);
        if (injectCtrl)
        {
            AddKeyInput(ChordlKeys.VK_LCONTROL, keyUp: false);
        }

        if (injectShift)
        {
            AddKeyInput(ChordlKeys.VK_LSHIFT, keyUp: false);
        }

        AddKeyInput(vkCode, keyUp: false);
        AddKeyInput(vkCode, keyUp: true);
        if (injectShift)
        {
            AddKeyInput(ChordlKeys.VK_LSHIFT, keyUp: true);
        }

        if (injectCtrl)
        {
            AddKeyInput(ChordlKeys.VK_LCONTROL, keyUp: true);
        }

        Marshal.SetLastPInvokeError(0);
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != (uint)inputs.Length)
        {
            log?.Invoke($"Warning: synthetic Chordl input sent {sent}/{inputs.Length} events. Error: {Marshal.GetLastPInvokeError()}.");
            return false;
        }

        return true;

        void AddKeyInput(int keyCode, bool keyUp)
        {
            inputs[index++] = KeyInput(keyCode, keyUp);
        }
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
        public bool AnyCtrlDown => LeftCtrl || RightCtrl;
        public bool AnyShiftDown => LeftShift || RightShift;

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

