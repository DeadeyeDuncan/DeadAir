using System.Runtime.InteropServices;

namespace DeadAir.Core.Inject;

public static class NativeInput
{
    public readonly record struct UnicodeUnit(ushort Code, bool IsReturn);

    /// <summary>Pure planning step for SendInput. .NET strings are UTF-16, so
    /// surrogate pairs (emoji etc.) already arrive as two char units — exactly
    /// what KEYEVENTF_UNICODE requires. Newlines map to VK_RETURN.</summary>
    public static UnicodeUnit[] BuildUnicodeInputs(string text)
    {
        var list = new List<UnicodeUnit>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                list.Add(new UnicodeUnit(0, true));
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (c == '\n') list.Add(new UnicodeUnit(0, true));
            else list.Add(new UnicodeUnit(c, false));
        }
        return list.ToArray();
    }

    // ---- Win32 SendInput plumbing (not unit-tested; exercised manually) ----

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public int type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    /// <summary>Marshalled size of the Win32 INPUT struct — must be 40 on x64.
    /// SendInput validates cbSize and silently no-ops (returns 0) on mismatch.</summary>
    public static int InputStructSize => Marshal.SizeOf<INPUT>();

    private static INPUT Key(ushort vk, ushort scan, uint flags) => new()
    { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT
        { wVk = vk, wScan = scan, dwFlags = flags } } };

    public static bool SendUnicodeText(string text)
    {
        foreach (var u in BuildUnicodeInputs(text))
        {
            INPUT[] pair = u.IsReturn
                ? new[] { Key(VK_RETURN, 0, 0),
                          Key(VK_RETURN, 0, KEYEVENTF_KEYUP) }
                : new[] { Key(0, u.Code, KEYEVENTF_UNICODE),
                          Key(0, u.Code, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP) };
            if (SendInput((uint)pair.Length, pair,
                    Marshal.SizeOf<INPUT>()) != pair.Length)
                return false;
        }
        return true;
    }

    public static bool SendCtrlV()
    {
        var seq = new[]
        {
            Key(VK_CONTROL, 0, 0), Key(VK_V, 0, 0),
            Key(VK_V, 0, KEYEVENTF_KEYUP), Key(VK_CONTROL, 0, KEYEVENTF_KEYUP),
        };
        return SendInput((uint)seq.Length, seq, Marshal.SizeOf<INPUT>())
            == seq.Length;
    }
}
