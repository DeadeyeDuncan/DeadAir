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

    [Fact]
    public void ConfigCommand_Json_UnaffectedByHostOnlySettings()
    {
        // Pins the contract Task 6b's dedup relies on: host-only settings must
        // not change the sidecar config payload, ASR settings must.
        var cfg = new AppConfig();
        var before = JsonSerializer.Serialize(ConfigCommand.From(cfg));
        cfg.Cleanup.OutputLanguage = "Spanish";
        cfg.Cleanup.Mode = CleanupMode.Polished;
        cfg.Ollama.Model = "someother:3b";
        cfg.Prompts.TranslationTemplate = "changed";
        var after = JsonSerializer.Serialize(ConfigCommand.From(cfg));
        Assert.Equal(before, after);

        cfg.Asr.Engine = "cpu";
        Assert.NotEqual(after, JsonSerializer.Serialize(ConfigCommand.From(cfg)));
    }
}
