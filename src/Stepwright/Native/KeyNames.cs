namespace Stepwright.Native;

/// <summary>Turns virtual key codes into the label a person would read out loud.</summary>
public static class KeyNames
{
    private static readonly Dictionary<uint, string> Special = new()
    {
        [0x08] = "Backspace",
        [0x09] = "Tab",
        [0x0D] = "Enter",
        [0x13] = "Pause",
        [0x1B] = "Esc",
        [0x20] = "Space",
        [0x21] = "Page Up",
        [0x22] = "Page Down",
        [0x23] = "End",
        [0x24] = "Home",
        [0x25] = "Left arrow",
        [0x26] = "Up arrow",
        [0x27] = "Right arrow",
        [0x28] = "Down arrow",
        [0x2C] = "Print Screen",
        [0x2D] = "Insert",
        [0x2E] = "Delete",
        [0x5D] = "Menu",
        [0x90] = "Num Lock",
        [0x91] = "Scroll Lock",
        [0xBA] = ";",
        [0xBB] = "equals",
        [0xBC] = ",",
        [0xBD] = "minus",
        [0xBE] = ".",
        [0xBF] = "/",
        [0xC0] = "`",
        [0xDB] = "[",
        [0xDC] = "\\",
        [0xDD] = "]",
        [0xDE] = "'",
    };

    public static string Describe(uint virtualKey)
    {
        if (Special.TryGetValue(virtualKey, out string? special))
        {
            return special;
        }

        if (virtualKey >= 0x30 && virtualKey <= 0x39)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey >= 0x41 && virtualKey <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey >= 0x60 && virtualKey <= 0x69)
        {
            return "Numpad " + (virtualKey - 0x60);
        }

        if (virtualKey >= 0x70 && virtualKey <= 0x87)
        {
            return "F" + (virtualKey - 0x6F);
        }

        return virtualKey switch
        {
            0x6A => "Numpad multiply",
            0x6B => "Numpad plus",
            0x6D => "Numpad minus",
            0x6E => "Numpad point",
            0x6F => "Numpad divide",
            _ => "key " + virtualKey,
        };
    }

    /// <summary>True when the key produces no text and is worth recording as its own step.</summary>
    public static bool IsNotableCommandKey(uint virtualKey)
    {
        return virtualKey is 0x0D or 0x09 or 0x1B or 0x2E or 0x08
            or 0x21 or 0x22 or 0x23 or 0x24
            or 0x25 or 0x26 or 0x27 or 0x28
            || (virtualKey >= 0x70 && virtualKey <= 0x87);
    }

    public static bool IsModifier(uint virtualKey)
    {
        return virtualKey is NativeMethods.VK_SHIFT or NativeMethods.VK_CONTROL or NativeMethods.VK_MENU
            or NativeMethods.VK_LWIN or NativeMethods.VK_RWIN or NativeMethods.VK_CAPITAL
            or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT
            or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL
            or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU;
    }

    public static string DescribeCombination(bool control, bool alt, bool shift, bool windows, uint virtualKey)
    {
        var parts = new List<string>(4);
        if (control)
        {
            parts.Add("Ctrl");
        }

        if (windows)
        {
            parts.Add("Windows");
        }

        if (alt)
        {
            parts.Add("Alt");
        }

        if (shift)
        {
            parts.Add("Shift");
        }

        parts.Add(Describe(virtualKey));
        return string.Join(" + ", parts);
    }
}
