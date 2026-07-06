using DeadAir.Core.Config;

namespace DeadAir.Core.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var cfg = ConfigStore.Load(Path.Combine(Path.GetTempPath(),
            Guid.NewGuid().ToString(), "config.json"));
        Assert.Equal("RControl", cfg.Hotkey.Key);
        Assert.Equal(CleanupMode.Faithful, cfg.Cleanup.Mode);
        Assert.Equal(50, cfg.Cleanup.SkipGuardChars);
        Assert.Equal("qwen2.5:7b", cfg.Ollama.Model);
        Assert.Equal(8192, cfg.Ollama.NumCtx);
        Assert.Equal("30m", cfg.Ollama.KeepAlive);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(),
            Guid.NewGuid().ToString(), "config.json");
        var cfg = new AppConfig();
        cfg.Dictionary.Add("DeadMind");
        cfg.Cleanup.Mode = CleanupMode.Polished;
        ConfigStore.Save(cfg, path);
        var rawJson = File.ReadAllText(path);
        Assert.Contains("\"Polished\"", rawJson);
        var loaded = ConfigStore.Load(path);
        Assert.Contains("DeadMind", loaded.Dictionary);
        Assert.Equal(CleanupMode.Polished, loaded.Cleanup.Mode);
    }
}
