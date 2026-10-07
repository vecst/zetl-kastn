namespace ZETL;

/// <summary>
/// Maps Linux evdev key codes (KEY_*) to the Windows virtual-key codes Chordl
/// speaks, and back for replayed chords. Codes are positional, as on Windows:
/// the OEM punctuation keys follow the US layout positions. Keys without a
/// mapping (media and vendor keys) are forwarded without consulting Chordl.
/// </summary>
internal static class LinuxKeyMap
{
    public const ushort KeyEsc = 1;
    public const ushort KeyLeftCtrl = 29;
    public const ushort KeyA = 30;
    public const ushort KeyLeftShift = 42;
    public const ushort KeyC = 46;
    public const ushort KeyV = 47;
    public const ushort KeyRightShift = 54;
    public const ushort KeyRightCtrl = 97;

    private static readonly Dictionary<ushort, int> ToVirtual = Build();
    private static readonly Dictionary<int, ushort> ToLinux = ToVirtual
        .GroupBy(pair => pair.Value)
        .ToDictionary(group => group.Key, group => group.Min(pair => pair.Key));

    public static bool TryGetVirtualKey(ushort linuxKey, out int virtualKey) =>
        ToVirtual.TryGetValue(linuxKey, out virtualKey);

    public static bool TryGetLinuxKey(int virtualKey, out ushort linuxKey) =>
        ToLinux.TryGetValue(virtualKey, out linuxKey);

    private static Dictionary<ushort, int> Build()
    {
        var map = new Dictionary<ushort, int>
        {
            [1] = 0x1B,   // Esc
            [11] = 0x30,  // 0
            [12] = 0xBD,  // -
            [13] = 0xBB,  // =
            [14] = 0x08,  // Backspace
            [15] = 0x09,  // Tab
            [26] = 0xDB,  // [
            [27] = 0xDD,  // ]
            [28] = 0x0D,  // Enter
            [29] = 0xA2,  // Left Ctrl
            [39] = 0xBA,  // ;
            [40] = 0xDE,  // '
            [41] = 0xC0,  // `
            [42] = 0xA0,  // Left Shift
            [43] = 0xDC,  // \
            [51] = 0xBC,  // ,
            [52] = 0xBE,  // .
            [53] = 0xBF,  // /
            [54] = 0xA1,  // Right Shift
            [55] = 0x6A,  // Keypad *
            [56] = 0xA4,  // Left Alt
            [57] = 0x20,  // Space
            [58] = 0x14,  // Caps Lock
            [69] = 0x90,  // Num Lock
            [70] = 0x91,  // Scroll Lock
            [71] = 0x67,  // Keypad 7
            [72] = 0x68,  // Keypad 8
            [73] = 0x69,  // Keypad 9
            [74] = 0x6D,  // Keypad -
            [75] = 0x64,  // Keypad 4
            [76] = 0x65,  // Keypad 5
            [77] = 0x66,  // Keypad 6
            [78] = 0x6B,  // Keypad +
            [79] = 0x61,  // Keypad 1
            [80] = 0x62,  // Keypad 2
            [81] = 0x63,  // Keypad 3
            [82] = 0x60,  // Keypad 0
            [83] = 0x6E,  // Keypad .
            [86] = 0xE2,  // 102nd key
            [87] = 0x7A,  // F11
            [88] = 0x7B,  // F12
            [96] = 0x0D,  // Keypad Enter
            [97] = 0xA3,  // Right Ctrl
            [98] = 0x6F,  // Keypad /
            [99] = 0x2C,  // Print Screen
            [100] = 0xA5, // Right Alt
            [102] = 0x24, // Home
            [103] = 0x26, // Up
            [104] = 0x21, // Page Up
            [105] = 0x25, // Left
            [106] = 0x27, // Right
            [107] = 0x23, // End
            [108] = 0x28, // Down
            [109] = 0x22, // Page Down
            [110] = 0x2D, // Insert
            [111] = 0x2E, // Delete
            [113] = 0xAD, // Mute
            [114] = 0xAE, // Volume Down
            [115] = 0xAF, // Volume Up
            [119] = 0x13, // Pause
            [125] = 0x5B, // Left Meta
            [126] = 0x5C, // Right Meta
            [127] = 0x5D, // Menu
            [163] = 0xB0, // Next track
            [164] = 0xB3, // Play/Pause
            [165] = 0xB1, // Previous track
            [166] = 0xB2, // Stop
        };

        // Digits 1-9 are KEY_1 (2) .. KEY_9 (10).
        for (var digit = 1; digit <= 9; digit++) map[(ushort)(digit + 1)] = '0' + digit;

        // Letter rows in physical order.
        AddRow(map, 16, "QWERTYUIOP");
        AddRow(map, 30, "ASDFGHJKL");
        AddRow(map, 44, "ZXCVBNM");

        // F1-F10 are contiguous from 59; F13-F24 from 183.
        for (var f = 0; f < 10; f++) map[(ushort)(59 + f)] = 0x70 + f;
        for (var f = 0; f < 12; f++) map[(ushort)(183 + f)] = 0x7C + f;
        return map;
    }

    private static void AddRow(Dictionary<ushort, int> map, ushort first, string letters)
    {
        for (var i = 0; i < letters.Length; i++) map[(ushort)(first + i)] = letters[i];
    }
}
