using System.Text.Json;
using DeadAir.Core.Sidecar;

namespace DeadAir.Core.Tests;

public class SidecarEventTests
{
    [Fact]
    public void ParsesWaveformSamples()
    {
        var e = JsonSerializer.Deserialize<SidecarEvent>(
            "{\"event\":\"waveform\",\"samples\":[-0.5,0.5,0.0,0.25]}");
        Assert.Equal("waveform", e!.Event);
        Assert.Equal(4, e.Samples!.Length);
        Assert.Equal(-0.5, e.Samples[0], 3);
    }

    [Fact]
    public void ParsesPartialTextAndSeq()
    {
        var e = JsonSerializer.Deserialize<SidecarEvent>(
            "{\"event\":\"partial\",\"text\":\"hello there\",\"seq\":3}");
        Assert.Equal("partial", e!.Event);
        Assert.Equal("hello there", e.Text);
        Assert.Equal(3, e.Seq!.Value);
    }
}
