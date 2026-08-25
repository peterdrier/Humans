using AwesomeAssertions;
using Humans.MailerLite.Services.Audiences;
using NodaTime;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Tests.Audiences;

public class MailerLiteAudienceBaseTests
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

        var members = await audience.ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        members.Should().BeEquivalentTo([optedIn, noPref]);
    }

    // Which ids survive with no opt-outs is already covered by the test above; the branch
    // only this test reaches is the no-copy one — with nothing to exclude, the base hands
    // back the subclass's own set rather than rebuilding it.
    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_NoOptOuts_ReturnsTheRawSetItself()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var raw = new HashSet<Guid> { a, b };

        var audience = NewAudience(
            raw: raw,
            infos: [InfoWithMarketing(a, optedOut: null), InfoWithMarketing(b, optedOut: false)]);

        var members = await audience.ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        members.Should().BeSameAs(raw,
            "with no opted-out users there is nothing to exclude, so the base returns the raw set");
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_EmptyRaw_DoesNotEnumerateUsers()
    {
        var users = Substitute.For<IUserService>();
        var audience = new FakeAudience([], users);

        var members = await audience.ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

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
        IReadOnlyList<CommunicationPreferenceInfo> prefs = optedOut is null
            ? []
            :
            [
                UserFixtures.Preference(
                    category: MessageCategory.Marketing,
                    optedOut: optedOut.Value,
                    updatedAt: Instant.FromUnixTimeSeconds(0),
                    updateSource: "Test"),
            ];

        return UserInfo.Create(
            new User { Id = userId, DisplayName = "u", PreferredLanguage = "en" },
            [], [], [], profile: null, prefs);
    }

    private sealed class FakeAudience(HashSet<Guid> raw, IUserService users)
        : MailerLiteAudienceBase(users)
    {
        public override string Key => "fake";
        public override string DisplayName => "Fake";
        public override string MailerLiteGroupName => "Humans - Fake";

        protected override Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlySet<Guid>>(raw);
    }
}
