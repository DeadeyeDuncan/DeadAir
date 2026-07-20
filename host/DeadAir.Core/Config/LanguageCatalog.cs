namespace DeadAir.Core.Config;

public static class LanguageCatalog
{
    // "English" first = translation off; then by speaker count.
    public static readonly IReadOnlyList<string> Languages =
    [
        "English", "Spanish", "Mandarin Chinese", "Hindi", "Arabic",
        "French", "Bengali", "Portuguese", "Russian", "Urdu",
        "Indonesian", "German",
    ];
}
