using System.Text.Json;
using DeadAir.Core.Config;
using DeadAir.Core.Sidecar;

namespace DeadAir.Core.Tests;

public class PillConfigTests
{
    [Fact]
    public void Default_Skin_IsNebula()
        => Assert.Equal("nebula", new AppConfig().Pill.Skin);

    [Fact]
    public void Skin_RoundTripsThroughJson()
    {
        var cfg = new AppConfig();
        cfg.Pill.Skin = "lantern";
        var back = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(cfg))!;
        Assert.Equal("lantern", back.Pill.Skin);
    }

    [Fact]
    public void SidecarConfigCommand_DoesNotCarryTheSkin()
    {
        var cfg = new AppConfig();
        cfg.Pill.Skin = "lantern";
        var json = JsonSerializer.Serialize(ConfigCommand.From(cfg));
        Assert.DoesNotContain("skin", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pill", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_Tuning_MatchesShippedConstants()
    {
        var p = new AppConfig().Pill;
        Assert.Equal(3.0, p.FanGain);
        Assert.Equal(0.6, p.Wiggle);
        Assert.Equal(1.0, p.WiggleSpeed);
    }

    [Fact]
    public void Tuning_RoundTripsThroughJson()
    {
        var cfg = new AppConfig();
        cfg.Pill.FanGain = 5.5;
        cfg.Pill.Wiggle = 1.2;
        cfg.Pill.WiggleSpeed = 2.5;
        var back = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(cfg))!;
        Assert.Equal(5.5, back.Pill.FanGain);
        Assert.Equal(1.2, back.Pill.Wiggle);
        Assert.Equal(2.5, back.Pill.WiggleSpeed);
    }
}
