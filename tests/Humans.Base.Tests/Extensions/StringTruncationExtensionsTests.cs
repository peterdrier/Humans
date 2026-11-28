using AwesomeAssertions;
using Humans.Base.Extensions;
using Xunit;

namespace Humans.Base.Tests.Extensions;

public class StringTruncationExtensionsTests
{
    [HumansTheory]
    [InlineData("abc", 3, "", "abc")]
    [InlineData("abcd", 3, "", "abc")]
    [InlineData("abcd", 3, "…", "ab…")]
    [InlineData("ab😀", 3, "", "ab")]
    [InlineData("a😀cd", 3, "", "a😀")]
    [InlineData("a😀cd", 3, "…", "a…")]
    public void Truncate_LongText_FitsWithoutSplittingPair(string text, int maxLength, string suffix, string expected) =>
        text.Truncate(maxLength, suffix).Should().Be(expected);
}
