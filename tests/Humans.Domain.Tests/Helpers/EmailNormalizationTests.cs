using AwesomeAssertions;
using Humans.Domain.Helpers;
using Xunit;

namespace Humans.Domain.Tests.Helpers;

public class EmailNormalizationTests
{
    [HumansTheory]
    [InlineData("user@googlemail.com", "user@gmail.com")]
    [InlineData("User@GoogleMail.COM", "user@gmail.com")]
    [InlineData("foo.bar@googlemail.com", "foo.bar@gmail.com")]
    public void NormalizeForComparison_GooglemailToGmail(string input, string expected)
    {
        EmailNormalization.NormalizeForComparison(input).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData("user@gmail.com", "user@gmail.com")]
    [InlineData("user@outlook.com", "user@outlook.com")]
    [InlineData("user@nobodies.team", "user@nobodies.team")]
    public void NormalizeForComparison_NonGooglemail_LowercasedUnchanged(string input, string expected)
    {
        EmailNormalization.NormalizeForComparison(input).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    public void NormalizeForComparison_NullOrEmpty_ReturnsInput(string? input)
    {
        EmailNormalization.NormalizeForComparison(input!).Should().Be(input);
    }
}
