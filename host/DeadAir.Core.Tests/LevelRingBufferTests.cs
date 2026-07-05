using System.Text.Json;
using DeadAir.Core;
using DeadAir.Core.Sidecar;

namespace DeadAir.Core.Tests;

public class LevelRingBufferTests
{
    [Fact]
    public void StartsAtFloor_PushShiftsLeft()
    {
        var buf = new LevelRingBuffer(4);
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 0.0 }, buf.Values);
        buf.Push(0.5);
        buf.Push(0.9);
        Assert.Equal(new[] { 0.0, 0.0, 0.5, 0.9 }, buf.Values);
    }

    [Fact]
    public void Reset_ReturnsToFloor()
    {
        var buf = new LevelRingBuffer(3);
        buf.Push(1.0);
        buf.Reset();
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, buf.Values);
    }

    [Fact]
    public void SidecarEvent_ParsesRms()
    {
        var e = JsonSerializer.Deserialize<SidecarEvent>(
            "{\"event\":\"level\",\"rms\":0.42}");
        Assert.Equal("level", e!.Event);
        Assert.Equal(0.42, e.Rms!.Value, 3);
    }
}
