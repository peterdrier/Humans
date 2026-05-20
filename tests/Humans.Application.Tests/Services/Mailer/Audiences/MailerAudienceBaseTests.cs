using AwesomeAssertions;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Mailer.Audiences;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Mailer.Audiences;

public class MailerAudienceBaseTests
{
    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_ExcludesExplicitMarketingOptOuts_KeepsNullAndOptIn()
    {
        var optedIn = Guid.NewGuid();   // MarketingOptedOut == false → kept
        var noPref = Guid.NewGuid();    // MarketingOptedOut == null  → kept
        var optedOut = Guid.NewGuid();  // MarketingOptedOut == true  → removed

        var audience = NewAudience(
            raw: [optedIn, noPref, optedOut],
            infos:
            [
                InfoWithMarketing(optedIn, optedOut: false),
                InfoWithMarketing(noPref, optedOut: null),
                InfoWithMarketing(optedOut, optedOut: true),
            ]);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEquivalentTo([optedIn, noPref]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_NoOptOuts_ReturnsRawUnchanged()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var audience = NewAudience(
            raw: [a, b],
            infos: [InfoWithMarketing(a, optedOut: null), InfoWithMarketing(b, optedOut: false)]);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEquivalentTo([a, b]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_EmptyRaw_DoesNotEnumerateUsers()
    {
        var users = Substitute.For<IUserService>();
        var audience = new FakeAudience([], users);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEmpty();
        await users.DidNotReceive().GetAllUserInfosAsync(Arg.Any<CancellationToken>());
    }

    private static FakeAudience NewAudience(HashSet<Guid> raw, List<UserInfo> infos)
    {
        var users = Substitute.For<IUserService>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(infos);
        return new FakeAudience(raw, users);
    }

    private static UserInfo InfoWithMarketing(Guid userId, bool? optedOut)
    {
        IReadOnlyList<CommunicationPreference> prefs = optedOut is null
            ? []
            :
            [
                new CommunicationPreference
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Category = MessageCategory.Marketing,
                    OptedOut = optedOut.Value,
                    UpdatedAt = Instant.FromUnixTimeSeconds(0),
                    UpdateSource = "Test",
                },
            ];

        return UserInfo.Create(
            new User { Id = userId, DisplayName = "u", PreferredLanguage = "en" },
            [], [], [], profile: null, [], [], [], prefs);
    }

    private sealed class FakeAudience(HashSet<Guid> raw, IUserService users)
        : MailerAudienceBase(users)
    {
        public override string Key => "fake";
        public override string DisplayName => "Fake";
        public override string MailerLiteGroupName => "Humans - Fake";

        protected override Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlySet<Guid>>(raw);
    }
}
