namespace InputFinder;

static class KeyNames
{
    public const int Vk5 = 0x35;
    public const int VkNumpad5 = 0x65;
    public const int ScanNumberRow5 = 0x06;

    public static bool IsNumberRow5(int vk, int scan)
    {
        if (vk == Vk5)
            return true;
        return vk is 0 or 0xFF && (scan & 0xFF) == ScanNumberRow5;
    }

    public static bool IsNumpad5(int vk, int scan)
    {
        if (vk == VkNumpad5)
            return true;
        return vk is 0 or 0xFF && (scan & 0xFF) == 0x4C;
    }

    public static string Describe(int vk, int scan)
    {
        if (IsNumberRow5(vk, scan))
            return "5 (number row)";
        if (IsNumpad5(vk, scan))
            return "Numpad 5";

        var named = vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x10 => "Shift",
            0x11 => "Ctrl",
            0x12 => "Alt",
            0x13 => "Pause",
            0x14 => "Caps Lock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2C => "Print Screen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x5B => "Left Win",
            0x5C => "Right Win",
            0x5D => "Menu",
            0x60 => "Numpad 0",
            0x61 => "Numpad 1",
            0x62 => "Numpad 2",
            0x63 => "Numpad 3",
            0x64 => "Numpad 4",
            0x66 => "Numpad 6",
            0x67 => "Numpad 7",
            0x68 => "Numpad 8",
            0x69 => "Numpad 9",
            0x6A => "Numpad *",
            0x6B => "Numpad +",
            0x6D => "Numpad -",
            0x6E => "Numpad .",
            0x6F => "Numpad /",
            0x70 => "F1",
            0x71 => "F2",
            0x72 => "F3",
            0x73 => "F4",
            0x74 => "F5",
            0x75 => "F6",
            0x76 => "F7",
            0x77 => "F8",
            0x78 => "F9",
            0x79 => "F10",
            0x7A => "F11",
            0x7B => "F12",
            0x90 => "Num Lock",
            0x91 => "Scroll Lock",
            0xA0 => "Left Shift",
            0xA1 => "Right Shift",
            0xA2 => "Left Ctrl",
            0xA3 => "Right Ctrl",
            0xA4 => "Left Alt",
            0xA5 => "Right Alt",
            _ => null,
        };
        if (named != null)
            return named;
        if (vk is >= 0x30 and <= 0x39)
            return ((char)vk).ToString();
        if (vk is >= 0x41 and <= 0x5A)
            return ((char)vk).ToString();
        if (vk is 0 or 0xFF)
            return $"scan 0x{(scan & 0xFF):X2}";
        return $"VK 0x{vk:X2}";
    }
}
