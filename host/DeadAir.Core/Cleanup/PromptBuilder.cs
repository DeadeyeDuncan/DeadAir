using DeadAir.Core.Config;

namespace DeadAir.Core.Cleanup;

public static class PromptBuilder
{
    public static string Build(CleanupMode mode, AppConfig cfg)
    {
        var basePrompt = mode == CleanupMode.Faithful
            ? cfg.Prompts.Faithful : cfg.Prompts.Polished;
        if (cfg.Dictionary.Count == 0) return basePrompt;
        return basePrompt +
            "\nPreserve these terms exactly, correcting near-misspellings " +
            "to them: " + string.Join(", ", cfg.Dictionary) + ".";
    }
}
