using DeadAir.Core.Config;

namespace DeadAir.Core.Tests;

public class TranslationMenuBuilderTests
{
    [Fact]
    public void Build_English_UsesOffHeaderAndChecksFirstCatalogChild()
    {
        var state = TranslationMenuBuilder.Build("English");

        Assert.Equal("Translate → Off", state.Header);
        Assert.Equal(12, state.Options.Count);
        Assert.Equal("Off (English)", state.Options[0].Header);
        Assert.Equal("English", state.Options[0].OutputLanguage);
        Assert.True(state.Options[0].IsChecked);
        Assert.Single(state.Options.Where(option => option.IsChecked));
    }

    [Fact]
    public void Build_CatalogLanguage_UsesLanguageHeaderAndChecksMatchingChild()
    {
        var state = TranslationMenuBuilder.Build("Hindi");

        Assert.Equal("Translate → Hindi", state.Header);
        var selected = Assert.Single(state.Options.Where(option => option.IsChecked));
        Assert.Equal("Hindi", selected.Header);
        Assert.Equal("Hindi", selected.OutputLanguage);
    }

    [Fact]
    public void Build_HandEditedLanguage_AppendsAndChecksExtraChild()
    {
        var state = TranslationMenuBuilder.Build("  Klingon  ");

        Assert.Equal("Translate → Klingon", state.Header);
        Assert.Equal(13, state.Options.Count);
        Assert.Equal("Klingon", state.Options[^1].Header);
        Assert.Equal("Klingon", state.Options[^1].OutputLanguage);
        Assert.True(state.Options[^1].IsChecked);
        Assert.Single(state.Options.Where(option => option.IsChecked));
    }

    [Fact]
    public void Build_CatalogMatchIsCaseInsensitiveAndUsesCanonicalLabel()
    {
        var state = TranslationMenuBuilder.Build("  arabic  ");

        Assert.Equal("Translate → Arabic", state.Header);
        Assert.Equal(12, state.Options.Count);
        var selected = Assert.Single(state.Options.Where(option => option.IsChecked));
        Assert.Equal("Arabic", selected.OutputLanguage);
    }
}
