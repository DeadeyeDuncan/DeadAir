using System.Runtime.InteropServices;
using System.Threading;

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

    // Win32 MSG. GetMessage writes the ENTIRE struct (48 bytes on x64) into the
    // caller's buffer — passing a bare `out nint` (8 bytes) overflows the stack
    // by 40 bytes and corrupts memory → AccessViolationException in the pump.
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    /// Size-pinned in a test: a wrong MSG size re-introduces the pump crash.
    public static int MessageStructSize => Marshal.SizeOf<MSG>();

    /// Size-pinned in a test: PtrToStructure reads through lParam with this
    /// layout — a drift from the 24-byte Win32 KBDLLHOOKSTRUCT misreads flags.
    public static int KbdllHookStructSize => Marshal.SizeOf<KBDLLHOOKSTRUCT>();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int id, HookProc proc,
        nint hMod, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode,
        nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, nint hWnd,
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
    private readonly ManualResetEventSlim _pumpReady = new(false);
    private nint _hook;
    private uint _threadId;
    private bool _disposed;

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
        _pumpReady.Set();
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
            {
                try
                {
                    _machine.OnKeyEvent((int)data.vkCode, isDown,
                        (data.flags & LLKHF_INJECTED) != 0);
                }
                catch
                {
                    // Subscribers must not break the global hook chain. Failures are
                    // intentionally swallowed here — if a subscriber throws, we still
                    // pass the event to the next hook via CallNextHookEx below.
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pumpReady.Wait(TimeSpan.FromSeconds(2));
        PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        _pumpReady.Dispose();
    }
}
