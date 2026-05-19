using AwesomeAssertions;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Mailer.Audiences;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Mailer.Audiences;

public class MarketingAudienceTests
{
    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_ReturnsOnlyExplicitMarketingOptIns()
    {
        var optedIn = Guid.NewGuid();     // OptedOut=false → IN
        var optedOut = Guid.NewGuid();    // OptedOut=true  → OUT
        var noPrefRow = Guid.NewGuid();   // no row         → OUT (default off)

        var audience = NewAudience(new[]
        {
            UserWithMarketingPref(optedIn, optedOut: false),
            UserWithMarketingPref(optedOut, optedOut: true),
            UserWithoutMarketingPref(noPrefRow),
        });

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEquivalentTo([optedIn]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_NoUsers_ReturnsEmpty()
    {
        var audience = NewAudience([]);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEmpty();
    }

    [HumansFact]
    public void Metadata_UsesHumansPrefix()
    {
        var audience = NewAudience([]);
        audience.Key.Should().Be("marketing");
        audience.MailerLiteGroupName.Should().Be("Humans - Marketing");
        audience.MailerLiteGroupName.Should().StartWith("Humans - ");
    }

    private static MarketingAudience NewAudience(IReadOnlyList<UserInfo> userInfos)
    {
        var users = Substitute.For<IUserService>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(userInfos);
        return new MarketingAudience(users);
    }

    private static UserInfo UserWithMarketingPref(Guid userId, bool optedOut) =>
        UserInfo.Create(
            new User { Id = userId, DisplayName = "u", PreferredLanguage = "en" },
            [], [], [], profile: null, [], [], [],
            [new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = MessageCategory.Marketing,
                OptedOut = optedOut,
                UpdatedAt = Instant.FromUnixTimeSeconds(0),
                UpdateSource = "Test",
            }]);

    private static UserInfo UserWithoutMarketingPref(Guid userId) =>
        UserInfoStubHelpers.MakeUserInfo(userId);
}
