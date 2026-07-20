using DeadAir.Core.Config;

namespace DeadAir.Core.Tests;

public class TranslationMenuBuilderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_BlankLanguage_MeansOff(string? outputLanguage)
    {
        var state = TranslationMenuBuilder.Build(outputLanguage);

        Assert.Equal("Translate → Off", state.Header);
        Assert.Equal(12, state.Options.Count);
        Assert.True(state.Options[0].IsChecked);
    }

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

    [Fact]
    public void Build_CatalogCurrent_ListsStickyHandEditedLanguageUnchecked()
    {
        var state = TranslationMenuBuilder.Build("English", "Klingon");

        Assert.Equal(13, state.Options.Count);
        Assert.True(state.Options[0].IsChecked);
        var extra = Assert.Single(state.Options.Where(option =>
            option.OutputLanguage == "Klingon"));
        Assert.False(extra.IsChecked);
        Assert.Single(state.Options.Where(option => option.IsChecked));
    }

    [Fact]
    public void Build_HandEditedCurrentAndSticky_ListsOneCheckedExtra()
    {
        var state = TranslationMenuBuilder.Build("Klingon", "Klingon");

        Assert.Equal(13, state.Options.Count);
        var extra = Assert.Single(state.Options.Where(option =>
            option.OutputLanguage == "Klingon"));
        Assert.True(extra.IsChecked);
        Assert.Single(state.Options.Where(option => option.IsChecked));
    }

    [Fact]
    public void Build_CatalogCurrent_DoesNotAppendCatalogMatchingStickyLanguage()
    {
        var state = TranslationMenuBuilder.Build("Hindi", "arabic");

        Assert.Equal(12, state.Options.Count);
        var selected = Assert.Single(state.Options.Where(option => option.IsChecked));
        Assert.Equal("Hindi", selected.OutputLanguage);
    }
}
