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

    [HumansTheory]
    [InlineData("peter+foo@gmail.com", "peter@gmail.com")]
    [InlineData("peter+travel+more@gmail.com", "peter@gmail.com")]
    [InlineData("Peter+Travel@Gmail.COM", "peter@gmail.com")]
    [InlineData("peter+foo@googlemail.com", "peter@gmail.com")]
    public void CanonicalizeGmail_StripsPlusTag(string input, string expected)
    {
        EmailNormalization.CanonicalizeGmail(input).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData("peter@gmail.com", "peter@gmail.com")]
    [InlineData("pet.er+foo@gmail.com", "pet.er@gmail.com")]
    [InlineData("peter+foo@outlook.com", "peter+foo@outlook.com")]
    [InlineData("user@nobodies.team", "user@nobodies.team")]
    public void CanonicalizeGmail_NonGmailOrNoTag_LeavesTagIntact(string input, string expected)
    {
        EmailNormalization.CanonicalizeGmail(input).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    public void CanonicalizeGmail_NullOrEmpty_ReturnsInput(string? input)
    {
        EmailNormalization.CanonicalizeGmail(input!).Should().Be(input);
    }
}
