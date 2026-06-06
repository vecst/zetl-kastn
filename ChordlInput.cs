using System.Runtime.InteropServices;
using Chordl;

namespace ZETL;

// Windows synthetic input: replays a Ctrl[+Shift]+<key> chord via SendInput.
// Lives with the Windows backend rather than the portable Chordl library; a
// Linux backend sends through uinput instead.
internal static class ChordlInput
{
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static bool SendCtrlChord(int vkCode, bool includeShift, bool restoreCtrl, bool restoreShift, Action<string>? log = null)
    {
        var inputCount =
            7
            + (includeShift ? 2 : 0)
            + (restoreCtrl ? 1 : 0)
            + (restoreShift ? 1 : 0);

        var inputs = new INPUT[inputCount];
        var index = 0;
        AddKeyInput(vkCode, keyUp: true);
        AddKeyInput(ChordlKeys.VK_SHIFT, keyUp: true);
        AddKeyInput(ChordlKeys.VK_CONTROL, keyUp: true);
        AddKeyInput(ChordlKeys.VK_CONTROL, keyUp: false);
        if (includeShift)
        {
            AddKeyInput(ChordlKeys.VK_SHIFT, keyUp: false);
        }

        AddKeyInput(vkCode, keyUp: false);
        AddKeyInput(vkCode, keyUp: true);
        if (includeShift)
        {
            AddKeyInput(ChordlKeys.VK_SHIFT, keyUp: true);
        }

        AddKeyInput(ChordlKeys.VK_CONTROL, keyUp: true);
        if (restoreCtrl)
        {
            AddKeyInput(ChordlKeys.VK_CONTROL, keyUp: false);
        }

        if (restoreShift)
        {
            AddKeyInput(ChordlKeys.VK_SHIFT, keyUp: false);
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

    public static bool SendPaste(Action<string>? log = null)
    {
        return SendCtrlChord(ChordlKeys.VK_V, includeShift: false, restoreCtrl: false, restoreShift: false, log);
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

