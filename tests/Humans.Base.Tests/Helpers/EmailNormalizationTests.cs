using AwesomeAssertions;
using Humans.Base.Helpers;
using Xunit;

namespace Humans.Base.Tests.Helpers;

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

    [HumansTheory]
    [InlineData("us.er@gmail.com", "user@gmail.com")]
    [InlineData("u.s.e.r@gmail.com", "user@gmail.com")]
    [InlineData("us.er+tag@gmail.com", "user@gmail.com")]
    [InlineData("Us.Er+Tag@GoogleMail.COM", "user@gmail.com")]
    [InlineData("user@gmail.com", "user@gmail.com")]
    public void CanonicalizeGmailAlias_StripsDotsAndPlusTag(string input, string expected)
    {
        EmailNormalization.CanonicalizeGmailAlias(input).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData("us.er@outlook.com", "us.er@outlook.com")]
    [InlineData("us.er+tag@outlook.com", "us.er+tag@outlook.com")]
    [InlineData("user@nobodies.team", "user@nobodies.team")]
    public void CanonicalizeGmailAlias_NonGmail_LeavesDotsAndTagIntact(string input, string expected)
    {
        EmailNormalization.CanonicalizeGmailAlias(input).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    public void CanonicalizeGmailAlias_NullOrEmpty_ReturnsInput(string? input)
    {
        EmailNormalization.CanonicalizeGmailAlias(input!).Should().Be(input);
    }

    // A dictionary keyed by this comparer is how a Google-reported canonical address
    // finds a user_emails row stored in an alias form (nobodies-collective/Humans#1101).
    // Equals and GetHashCode must agree or the entry lands in an unreachable bucket.
    [HumansTheory]
    [InlineData("us.er@gmail.com", "user@gmail.com")]
    [InlineData("us.er+tag@gmail.com", "user@gmail.com")]
    [InlineData("Us.Er+Tag@GoogleMail.COM", "user@gmail.com")]
    [InlineData("USER@NOBODIES.TEAM", "user@nobodies.team")]
    public void GmailAliasEmailComparer_TreatsAliasesAsTheSameAccount(string stored, string reported)
    {
        GmailAliasEmailComparer.Instance.Equals(stored, reported).Should().BeTrue();
        GmailAliasEmailComparer.Instance.GetHashCode(stored)
            .Should().Be(GmailAliasEmailComparer.Instance.GetHashCode(reported));
    }

    [HumansTheory]
    [InlineData("us.er@outlook.com", "user@outlook.com")]
    [InlineData("user+tag@outlook.com", "user@outlook.com")]
    [InlineData("other@gmail.com", "user@gmail.com")]
    public void GmailAliasEmailComparer_DoesNotFoldNonGmailAliases(string a, string b)
    {
        GmailAliasEmailComparer.Instance.Equals(a, b).Should().BeFalse();
    }

    [HumansFact]
    public void GmailAliasEmailComparer_KeyedDictionary_FindsAliasStoredRowByReportedAddress()
    {
        var byEmail = new Dictionary<string, int>(GmailAliasEmailComparer.Instance)
        {
            ["us.er+tag@googlemail.com"] = 42
        };

        byEmail.TryGetValue("user@gmail.com", out var value).Should().BeTrue();
        value.Should().Be(42);
    }
}
