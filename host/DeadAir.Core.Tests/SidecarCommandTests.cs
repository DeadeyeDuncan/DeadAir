using System.Text.Json;
using DeadAir.Core.Config;
using DeadAir.Core.Sidecar;

namespace DeadAir.Core.Tests;

public class SidecarCommandTests
{
    [Fact]
    public void ConfigCommand_CarriesPartialDefaults()
    {
        var cmd = ConfigCommand.From(new AppConfig());
        var json = JsonSerializer.Serialize(cmd);
        Assert.Contains("\"partials\":true", json);
        Assert.Contains("\"partial_interval_ms\":600", json);
        Assert.Contains("\"partial_min_ms\":700", json);
        Assert.Contains("\"partial_window_s\":30", json);
    }
}
