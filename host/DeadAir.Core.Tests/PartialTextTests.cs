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
}
