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
    public static TranslationMenuState Build(
        string? outputLanguage, string? stickyLanguage = null)
    {
        var requested = string.IsNullOrWhiteSpace(outputLanguage)
            ? "English"
            : outputLanguage.Trim();
        var catalogMatch = LanguageCatalog.Languages.FirstOrDefault(language =>
            string.Equals(language, requested,
                StringComparison.OrdinalIgnoreCase));
        var current = catalogMatch ?? requested;

        var languages = LanguageCatalog.Languages.ToList();
        var sticky = stickyLanguage?.Trim();
        var extra = catalogMatch is null
            ? current
            : !string.IsNullOrWhiteSpace(sticky) &&
              !LanguageCatalog.Languages.Contains(sticky,
                  StringComparer.OrdinalIgnoreCase)
                ? sticky
                : null;
        if (extra is not null)
            languages.Add(extra);

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
