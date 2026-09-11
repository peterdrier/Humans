using AwesomeAssertions;
using Humans.Tickets.Services;
using Humans.Users.Contracts;

namespace Humans.Tickets.Tests.Services;

/// <summary>
/// <see cref="VerifiedEmailLookup"/> is the one email → user index the sync and the
/// ticket-count fallback share: verified rows only, aliases folded, collisions map to nobody.
/// </summary>
public sealed class VerifiedEmailLookupTests
{
    private static UserInfo UserWith(Guid id, params (string Email, bool Verified)[] emails) =>
        new User { Id = id, DisplayName = id.ToString() }.ToUserInfo(
            emails.Select(e => new UserEmail { Id = Guid.NewGuid(), UserId = id, Email = e.Email, IsVerified = e.Verified }).ToList());

    [HumansFact]
    public void MapsAVerifiedEmailToItsOwner_FoldingCaseAndGoogleMail()
    {
        var owner = Guid.NewGuid();

        var (lookup, collisions) = VerifiedEmailLookup.Build([UserWith(owner, ("Ada@GoogleMail.com", true))]);

        lookup["ada@gmail.com"].Should().Be(owner);
        collisions.Should().BeEmpty();
    }

    [HumansFact]
    public void IgnoresUnverifiedEmails()
    {
        var (lookup, _) = VerifiedEmailLookup.Build([UserWith(Guid.NewGuid(), ("ada@example.com", false))]);

        lookup.Should().BeEmpty();
    }

    [HumansFact]
    public void AnEmailVerifiedByTwoUsersMapsToNobody_AndIsReported()
    {
        var (lookup, collisions) = VerifiedEmailLookup.Build([
            UserWith(Guid.NewGuid(), ("ada@gmail.com", true)),
            UserWith(Guid.NewGuid(), ("ada@googlemail.com", true)),
        ]);

        lookup.Should().BeEmpty();
        collisions.Should().ContainSingle().Which.UserCount.Should().Be(2);
    }

    [HumansFact]
    public void TheSameUserVerifyingBothAliasesIsNotACollision()
    {
        var owner = Guid.NewGuid();

        var (lookup, collisions) = VerifiedEmailLookup.Build([
            UserWith(owner, ("ada@gmail.com", true), ("ada@googlemail.com", true)),
        ]);

        lookup["ada@googlemail.com"].Should().Be(owner);
        collisions.Should().BeEmpty();
    }
}
