using DeadAir.Core;

namespace DeadAir.Core.Tests;

public class WaveformRingBufferTests
{
    [Fact]
    public void PushRange_KeepsLastCapacityOldestToNewest()
    {
        var buf = new WaveformRingBuffer(4);
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 0.0 }, buf.Values);
        buf.PushRange(new[] { 1.0, 2.0 });
        buf.PushRange(new[] { 3.0, 4.0, 5.0 });   // overflows by one
        Assert.Equal(new[] { 2.0, 3.0, 4.0, 5.0 }, buf.Values);
    }

    [Fact]
    public void Reset_ReturnsToZero()
    {
        var buf = new WaveformRingBuffer(3);
        buf.PushRange(new[] { 9.0, 9.0, 9.0 });
        buf.Reset();
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, buf.Values);
    }
}
