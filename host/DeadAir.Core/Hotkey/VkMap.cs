namespace DeadAir.Core.Hotkey;

public static class VkMap
{
    private static readonly Dictionary<string, int> Map =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["RControl"] = 0xA3, ["LControl"] = 0xA2,
        ["RAlt"] = 0xA5, ["LAlt"] = 0xA4,
        ["RShift"] = 0xA1, ["LShift"] = 0xA0,
        ["CapsLock"] = 0x14, ["Scroll"] = 0x91, ["Pause"] = 0x13,
        ["F13"] = 0x7C, ["F14"] = 0x7D, ["F15"] = 0x7E, ["F16"] = 0x7F,
        ["F17"] = 0x80, ["F18"] = 0x81, ["F19"] = 0x82, ["F20"] = 0x83,
        ["F21"] = 0x84, ["F22"] = 0x85, ["F23"] = 0x86, ["F24"] = 0x87,
    };

    public static int Resolve(string name) =>
        Map.TryGetValue(name, out var vk) ? vk
        : throw new ArgumentException($"unknown hotkey name: {name}");
}
