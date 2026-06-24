namespace Chordl;

public static class ChordlKeys
{
    public const int VK_CONTROL = 0x11;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_SHIFT = 0x10;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_A = 0x41;
    public const int VK_B = 0x42;
    public const int VK_C = 0x43;
    public const int VK_P = 0x50;
    public const int VK_R = 0x52;
    public const int VK_T = 0x54;
    public const int VK_V = 0x56;
    public const int VK_X = 0x58;
    public const int VK_Z = 0x5A;

    public static bool IsControlKey(int vkCode)
    {
        return vkCode is VK_CONTROL or VK_LCONTROL or VK_RCONTROL;
    }

    public static bool IsShiftKey(int vkCode)
    {
        return vkCode is VK_SHIFT or VK_LSHIFT or VK_RSHIFT;
    }

    public static string FormatComboName(int vkCode, bool includeShift)
    {
        var keyName = vkCode switch
        {
            VK_A => "A",
            VK_B => "B",
            VK_C => "C",
            VK_P => "P",
            VK_R => "R",
            VK_T => "T",
            VK_V => "V",
            VK_X => "X",
            VK_Z => "Z",
            _ => $"VK_{vkCode:X2}"
        };

        return includeShift ? $"Ctrl+Shift+{keyName}" : $"Ctrl+{keyName}";
    }

    public static int KeyToVirtualKey(string key)
    {
        if (TryKeyToVirtualKey(key, out var vkCode))
        {
            return vkCode;
        }

        throw new InvalidOperationException($"Unsupported Chordl key '{key}'.");
    }

    public static bool TryKeyToVirtualKey(string key, out int vkCode)
    {
        if (key.Length == 1 && char.IsLetter(key[0]))
        {
            vkCode = char.ToUpperInvariant(key[0]);
            return true;
        }

        vkCode = 0;
        return false;
    }
}
