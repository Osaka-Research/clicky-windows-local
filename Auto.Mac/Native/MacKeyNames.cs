namespace Auto.Mac.Native;

/// <summary>
/// Human-readable form of a Carbon modifier/keycode combination, e.g. "Ctrl+Shift+1" --
/// the Mac equivalent of the Windows build's Win32.DescribeHotkey. Standard Apple HIToolbox
/// virtual keycodes (kVK_ANSI_*), stable across macOS versions.
/// </summary>
internal static class MacKeyNames
{
    private static readonly Dictionary<uint, string> KeyNames = new()
    {
        [0x12] = "1", [0x13] = "2", [0x14] = "3", [0x15] = "4",
        [0x17] = "5", [0x16] = "6", [0x1A] = "7", [0x1C] = "8", [0x19] = "9", [0x1D] = "0",
        [0x00] = "A", [0x0B] = "B", [0x08] = "C", [0x02] = "D", [0x0E] = "E",
        [0x03] = "F", [0x05] = "G", [0x04] = "H", [0x22] = "I", [0x26] = "J",
        [0x28] = "K", [0x25] = "L", [0x2E] = "M", [0x2D] = "N", [0x1F] = "O",
        [0x23] = "P", [0x0C] = "Q", [0x0F] = "R", [0x01] = "S", [0x11] = "T",
        [0x20] = "U", [0x09] = "V", [0x0D] = "W", [0x07] = "X", [0x10] = "Y", [0x06] = "Z",
        [0x31] = "Space",
    };

    public static string Describe(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & Carbon.controlKey) != 0) parts.Add("Ctrl");
        if ((modifiers & Carbon.optionKey) != 0) parts.Add("Opt");
        if ((modifiers & Carbon.shiftKey) != 0) parts.Add("Shift");
        if ((modifiers & Carbon.cmdKey) != 0) parts.Add("Cmd");
        parts.Add(KeyNames.TryGetValue(virtualKey, out var name) ? name : $"Key(0x{virtualKey:X})");
        return string.Join("+", parts);
    }
}
