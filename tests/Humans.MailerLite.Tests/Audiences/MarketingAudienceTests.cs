using AwesomeAssertions;
using Humans.MailerLite.Tests.Infrastructure;
using Humans.MailerLite.Services.Audiences;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Tests.Audiences;

public class MarketingAudienceTests
{
    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_ReturnsOnlyExplicitMarketingOptIns()
    {
        var optedIn = Guid.NewGuid();     // OptedOut=false → IN
        var optedOut = Guid.NewGuid();    // OptedOut=true  → OUT
        var noPrefRow = Guid.NewGuid();   // no row         → OUT (default off)

        var audience = NewAudience(
            [User(optedIn), User(optedOut), User(noPrefRow)],
            (optedIn, false), (optedOut, true));

        var members = await audience.ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        members.Should().BeEquivalentTo([optedIn]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_NoUsers_ReturnsEmpty()
    {
        var audience = NewAudience([]);

        var members = await audience.ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        members.Should().BeEmpty();
    }

    [HumansFact]
    public void Metadata_UsesHumansPrefix()
    {
        var audience = NewAudience([]);
        audience.Key.Should().Be("marketing");
        audience.MailerLiteGroupName.Should().Be("Humans - Marketing");
    }

    private static MarketingAudience NewAudience(
        IReadOnlyList<UserInfo> userInfos, params (Guid UserId, bool OptedOut)[] marketing)
    {
        var users = Substitute.For<IUserService>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(userInfos);
        return new MarketingAudience(users, MarketingPreferenceStubs.For(marketing));
    }

    private static UserInfo User(Guid userId) =>
        UserInfoStubHelpers.MakeUserInfo(userId);
}
