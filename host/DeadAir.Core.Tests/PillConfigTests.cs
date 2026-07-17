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
}
