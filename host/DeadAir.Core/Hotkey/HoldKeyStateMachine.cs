namespace DeadAir.Core.Hotkey;

public sealed class HoldKeyStateMachine(int vkCode)
{
    private bool _held;

    public event Action? HoldStarted;
    public event Action? HoldEnded;

    public void OnKeyEvent(int vk, bool isDown, bool injected)
    {
        if (injected || vk != vkCode) return;
        if (isDown && !_held) { _held = true; HoldStarted?.Invoke(); }
        else if (!isDown && _held) { _held = false; HoldEnded?.Invoke(); }
    }
}
