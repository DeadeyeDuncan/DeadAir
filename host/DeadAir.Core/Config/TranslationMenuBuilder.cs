namespace DeadAir.Core.Config;

public sealed record TranslationMenuOption(
    string Header,
    string OutputLanguage,
    bool IsChecked);

public sealed record TranslationMenuState(
    string Header,
    IReadOnlyList<TranslationMenuOption> Options);

public static class TranslationMenuBuilder
{
    public static TranslationMenuState Build(string? outputLanguage)
    {
        var requested = string.IsNullOrWhiteSpace(outputLanguage)
            ? "English"
            : outputLanguage.Trim();
        var catalogMatch = LanguageCatalog.Languages.FirstOrDefault(language =>
            string.Equals(language, requested,
                StringComparison.OrdinalIgnoreCase));
        var current = catalogMatch ?? requested;

        var languages = LanguageCatalog.Languages.ToList();
        if (catalogMatch is null)
            languages.Add(current);

        var options = languages
            .Select(language => new TranslationMenuOption(
                string.Equals(language, "English",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Off (English)"
                    : language,
                language,
                string.Equals(language, current,
                    StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var parentHeader = string.Equals(current, "English",
            StringComparison.OrdinalIgnoreCase)
            ? "Translate → Off"
            : $"Translate → {current}";

        return new TranslationMenuState(parentHeader, options);
    }
}
