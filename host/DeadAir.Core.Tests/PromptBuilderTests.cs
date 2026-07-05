using DeadAir.Core.Cleanup;
using DeadAir.Core.Config;

namespace DeadAir.Core.Tests;

public class PromptBuilderTests
{
    [Fact]
    public void Faithful_NoDictionary_IsBasePrompt()
    {
        var cfg = new AppConfig();
        Assert.Equal(cfg.Prompts.Faithful,
            PromptBuilder.Build(CleanupMode.Faithful, cfg));
    }

    [Fact]
    public void Polished_WithDictionary_AppendsTerms()
    {
        var cfg = new AppConfig();
        cfg.Dictionary.AddRange(new[] { "DeadMind", "gfx1030" });
        var p = PromptBuilder.Build(CleanupMode.Polished, cfg);
        Assert.StartsWith(cfg.Prompts.Polished, p);
        Assert.Contains("DeadMind, gfx1030", p);
    }
}
