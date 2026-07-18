using DeadAir.Core.Config;

namespace DeadAir.Core.Cleanup;

public static class PromptBuilder
{
    public static string Build(CleanupMode mode, AppConfig cfg)
    {
        var prompt = mode == CleanupMode.Faithful
            ? cfg.Prompts.Faithful : cfg.Prompts.Polished;
        if (cfg.Cleanup.TranslationActive)
        {
            var style = mode == CleanupMode.Faithful
                ? "literal, preserving the speaker's register and tone"
                : "natural and fluent";
            prompt += "\n" + cfg.Prompts.TranslationTemplate
                .Replace("{language}", cfg.Cleanup.OutputLanguage!.Trim())
                .Replace("{style}", style);
        }
        if (cfg.Dictionary.Count == 0) return prompt;
        return prompt +
            "\nPreserve these terms exactly, correcting near-misspellings " +
            "to them: " + string.Join(", ", cfg.Dictionary) + ".";
    }
}
