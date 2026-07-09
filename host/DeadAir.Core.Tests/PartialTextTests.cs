using DeadAir.Core;

namespace DeadAir.Core.Tests;

public class PartialTextTests
{
    [Theory]
    [InlineData(null, "hello world", 0)]
    [InlineData("recognize speech", "recognize speech", 2)]
    [InlineData("wreck a nice", "wreck a beach", 2)]   // diverges at word 3
    [InlineData("a b c", "a b c d", 3)]
    public void CommonPrefixWords_CountsSharedLeadingWords(
        string? prev, string curr, int expected)
        => Assert.Equal(expected, PartialText.CommonPrefixWords(prev, curr));

    [Fact]
    public void LeftElide_KeepsNewestTailWithEllipsis()
    {
        Assert.Equal("short", PartialText.LeftElide("short", 10));
        Assert.Equal("…World", PartialText.LeftElide("Hello World", 6));
    }

    [Fact]
    public void SplitWords_SplitsOnAnyWhitespace()
    {
        Assert.Equal(new[] { "a", "b", "c" }, PartialText.SplitWords("a\tb\nc"));
        Assert.Equal(new[] { "x", "y" }, PartialText.SplitWords("  x   y  "));
        Assert.Empty(PartialText.SplitWords(null));
    }

    [Fact]
    public void CommonPrefixWords_IsWhitespaceAgnostic()
    {
        // A tab/newline in one string must not desync the prefix count
        // (the bug the shared tokenizer closes).
        Assert.Equal(2, PartialText.CommonPrefixWords("a b", "a\tb c"));
    }

    [Fact]
    public void LayoutInterim_AppendKeepsPrefixDimTailHot()
    {
        var l = PartialText.LayoutInterim("one two", "one two three", 46);
        Assert.Equal("one two", l.Dim);
        Assert.Equal("three", l.Hot);
    }

    [Fact]
    public void LayoutInterim_WholeLineRewriteIsAllHot()
    {
        var l = PartialText.LayoutInterim("aaa bbb", "xxx yyy", 46);
        Assert.Equal("", l.Dim);
        Assert.Equal("xxx yyy", l.Hot);
    }

    [Fact]
    public void LayoutInterim_LongHotTailElidedFromLeft()
    {
        // The changed tail alone exceeds the budget -> drop the stable head and
        // keep the tail's newest chars (newest words never clip).
        var l = PartialText.LayoutInterim("", "abcdefghij", 6);
        Assert.Equal("", l.Dim);
        Assert.Equal("…fghij", l.Hot);
    }

    [Fact]
    public void LayoutInterim_ElidesStableHeadToKeepHotVisible()
    {
        // budget 8: hot "cc" (2) + joining space -> stable budget 5 -> "…a bb"
        var l = PartialText.LayoutInterim("aaaaaa bb", "aaaaaa bb cc", 8);
        Assert.Equal("cc", l.Hot);          // newest fully visible
        Assert.Equal("…a bb", l.Dim);
    }
}
