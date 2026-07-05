using System.Runtime.InteropServices;

namespace DeadAir.Core.Hotkey;

public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    { public uint vkCode, scanCode, flags, time; public nint dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int id, HookProc proc,
        nint hMod, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode,
        nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern int GetMessageW(out nint msg, nint hWnd,
        uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint msg,
        nint wParam, nint lParam);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const uint WM_QUIT = 0x0012;

    private readonly HoldKeyStateMachine _machine;
    private readonly HookProc _proc; // rooted: prevents GC of the delegate
    private readonly Thread _thread;
    private nint _hook;
    private uint _threadId;

    public KeyboardHook(HoldKeyStateMachine machine)
    {
        _machine = machine;
        _proc = Callback;
        _thread = new Thread(RunPump) { IsBackground = true,
            Name = "DeadAir-KbHook" };
        _thread.Start();
    }

    private void RunPump()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, 0, 0);
        // Keep the callback trivial and this pump responsive: Windows silently
        // removes hooks whose callbacks time out (spec §D6).
        while (GetMessageW(out _, 0, 0, 0) > 0) { }
        if (_hook != 0) UnhookWindowsHookEx(_hook);
    }

    private nint Callback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var isDown = wParam is WM_KEYDOWN or WM_SYSKEYDOWN;
            var isUp = wParam is WM_KEYUP or WM_SYSKEYUP;
            if (isDown || isUp)
                _machine.OnKeyEvent((int)data.vkCode, isDown,
                    (data.flags & LLKHF_INJECTED) != 0);
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() =>
        PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
}
