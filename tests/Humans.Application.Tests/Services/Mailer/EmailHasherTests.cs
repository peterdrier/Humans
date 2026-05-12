using AwesomeAssertions;
using Humans.Application.Services.Mailer;

namespace Humans.Application.Tests.Services.Mailer;

public class EmailHasherTests
{
    [HumansFact]
    public void Hash_ProducesStableHexLengthOf64()
    {
        var h = EmailHasher.Hash("a@b.com");
        h.Should().HaveLength(64);
        h.Should().MatchRegex("^[0-9a-f]+$");
    }

    [HumansFact]
    public void Hash_IsCaseInsensitive_AndTrims()
    {
        EmailHasher.Hash("A@B.com").Should().Be(EmailHasher.Hash("a@b.com"));
        EmailHasher.Hash("  a@b.com  ").Should().Be(EmailHasher.Hash("a@b.com"));
    }

    [HumansFact]
    public void Hash_TreatsGmailAndGooglemailAsEquivalent()
    {
        // NormalizeForComparison rules from IUserEmailService — gmail/googlemail aliasing.
        EmailHasher.Hash("foo@gmail.com").Should().Be(EmailHasher.Hash("foo@googlemail.com"));
    }
}
