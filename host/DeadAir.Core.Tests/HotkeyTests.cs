using DeadAir.Core.Hotkey;

namespace DeadAir.Core.Tests;

public class HotkeyTests
{
    private const int VK = 0xA3; // RControl

    [Fact]
    public void DownFiresOnce_AutoRepeatIgnored_UpFires()
    {
        var sm = new HoldKeyStateMachine(VK);
        int started = 0, ended = 0;
        sm.HoldStarted += () => started++;
        sm.HoldEnded += () => ended++;

        sm.OnKeyEvent(VK, isDown: true, injected: false);
        sm.OnKeyEvent(VK, isDown: true, injected: false);  // auto-repeat
        sm.OnKeyEvent(VK, isDown: true, injected: false);
        sm.OnKeyEvent(VK, isDown: false, injected: false);

        Assert.Equal(1, started);
        Assert.Equal(1, ended);
    }

    [Fact]
    public void OtherKeysAndInjectedEventsIgnored()
    {
        var sm = new HoldKeyStateMachine(VK);
        int started = 0;
        sm.HoldStarted += () => started++;
        sm.OnKeyEvent(0x41, true, false);   // 'A'
        sm.OnKeyEvent(VK, true, injected: true); // our own SendInput echo
        Assert.Equal(0, started);
    }

    [Fact]
    public void VkMap_ResolvesKnown_ThrowsUnknown()
    {
        Assert.Equal(0xA3, VkMap.Resolve("RControl"));
        Assert.Equal(0x7C, VkMap.Resolve("F13"));
        Assert.Throws<ArgumentException>(() => VkMap.Resolve("SuperKey"));
    }
}
