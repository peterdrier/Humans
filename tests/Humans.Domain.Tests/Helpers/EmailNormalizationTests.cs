using AwesomeAssertions;
using Humans.Domain.Helpers;
using Xunit;

namespace Humans.Domain.Tests.Helpers;

public class EmailNormalizationTests
{
    [Theory]
    [InlineData("user@googlemail.com", "user@gmail.com")]
    [InlineData("User@GoogleMail.COM", "User@gmail.com")]
    [InlineData("foo.bar@googlemail.com", "foo.bar@gmail.com")]
    public void Canonicalize_GooglemailToGmail(string input, string expected)
    {
        EmailNormalization.Canonicalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("user@gmail.com")]
    [InlineData("user@outlook.com")]
    [InlineData("user@nobodies.team")]
    public void Canonicalize_NonGooglemail_Unchanged(string input)
    {
        EmailNormalization.Canonicalize(input).Should().Be(input);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Canonicalize_NullOrEmpty_ReturnsInput(string? input)
    {
        EmailNormalization.Canonicalize(input!).Should().Be(input);
    }
}
