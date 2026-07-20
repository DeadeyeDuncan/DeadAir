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

    [Fact]
    public void EnglishOutput_AnyCasing_NoTranslationDirective()
    {
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = "english";
        Assert.Equal(cfg.Prompts.Faithful,
            PromptBuilder.Build(CleanupMode.Faithful, cfg));
    }

    [Fact]
    public void SpanishOutput_AppendsFilledDirective_AfterBasePrompt()
    {
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = "Spanish";
        var p = PromptBuilder.Build(CleanupMode.Faithful, cfg);
        Assert.StartsWith(cfg.Prompts.Faithful, p);
        Assert.Contains("render the transcript in Spanish", p);
        Assert.Contains("ONLY the Spanish text", p);
        Assert.DoesNotContain("{language}", p);
        Assert.DoesNotContain("{style}", p);
    }

    [Fact]
    public void TranslationStyle_TracksCleanupMode()
    {
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = "Spanish";
        Assert.Contains("literal", PromptBuilder.Build(CleanupMode.Faithful, cfg));
        Assert.Contains("natural and fluent",
            PromptBuilder.Build(CleanupMode.Polished, cfg));
    }

    [Fact]
    public void SpanishOutput_DictionarySuffix_StaysLast()
    {
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = " Spanish ";
        cfg.Dictionary.Add("DeadMind");
        var p = PromptBuilder.Build(CleanupMode.Polished, cfg);
        Assert.Contains("render the transcript in Spanish", p);
        Assert.True(p.IndexOf("render the transcript") < p.IndexOf("DeadMind"),
            "dictionary suffix must come after the translation directive");
        Assert.EndsWith("DeadMind.", p);
    }

    [Theory]
    [InlineData("Mandarin Chinese")]
    [InlineData("Arabic")]
    public void NewLanguageOutput_AppendsFilledDirective(string language)
    {
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = language;

        var prompt = PromptBuilder.Build(CleanupMode.Faithful, cfg);

        Assert.Contains($"render the transcript in {language}", prompt);
        Assert.Contains($"ONLY the {language} text", prompt);
        Assert.DoesNotContain("{language}", prompt);
        Assert.DoesNotContain("{style}", prompt);
    }
}
