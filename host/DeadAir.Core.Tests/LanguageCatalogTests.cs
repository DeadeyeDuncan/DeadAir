using DeadAir.Core.Config;

namespace DeadAir.Core.Tests;

public class LanguageCatalogTests
{
    [Fact]
    public void Languages_EnglishIsFirst()
    {
        Assert.Equal("English", LanguageCatalog.Languages[0]);
    }

    [Fact]
    public void Languages_SpanishIsSecond()
    {
        Assert.Equal("Spanish", LanguageCatalog.Languages[1]);
    }

    [Fact]
    public void Languages_HasExactOrderedTwelveEntries()
    {
        Assert.Equal(new[]
        {
            "English", "Spanish", "Mandarin Chinese", "Hindi", "Arabic",
            "French", "Bengali", "Portuguese", "Russian", "Urdu",
            "Indonesian", "German",
        }, LanguageCatalog.Languages);
        Assert.Equal(12, LanguageCatalog.Languages.Count);
    }

    [Fact]
    public void Languages_HasNoCaseInsensitiveDuplicates()
    {
        Assert.Equal(LanguageCatalog.Languages.Count,
            LanguageCatalog.Languages
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }
}
