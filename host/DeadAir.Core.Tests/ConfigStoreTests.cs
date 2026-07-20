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
        Assert.Equal("qwen3:8b", cfg.Ollama.Model);
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

    [Fact]
    public void OutputLanguage_DefaultsToEnglish_TranslationOff()
    {
        var cfg = new AppConfig();
        Assert.Equal("English", cfg.Cleanup.OutputLanguage);
        Assert.False(cfg.Cleanup.TranslationActive);
    }

    [Theory]
    [InlineData("English", false)]
    [InlineData("english", false)]
    [InlineData("  ENGLISH  ", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("Spanish", true)]
    [InlineData("spanish", true)]
    [InlineData(" French ", true)]
    public void TranslationActive_ReflectsOutputLanguage(string? lang, bool active)
    {
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = lang!;
        Assert.Equal(active, cfg.Cleanup.TranslationActive);
    }

    [Fact]
    public void OutputLanguage_RoundTrips_MultiWordValue_AndTranslationActiveNotSerialized()
    {
        var path = Path.Combine(Path.GetTempPath(),
            Guid.NewGuid().ToString(), "config.json");
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = "Mandarin Chinese";
        ConfigStore.Save(cfg, path);
        var rawJson = File.ReadAllText(path);
        Assert.Contains("\"outputLanguage\": \"Mandarin Chinese\"", rawJson);
        Assert.DoesNotContain("translationActive", rawJson);
        Assert.Contains("translationTemplate", rawJson);
        var loaded = ConfigStore.Load(path);
        Assert.Equal("Mandarin Chinese", loaded.Cleanup.OutputLanguage);
        Assert.True(loaded.Cleanup.TranslationActive);
    }
}
