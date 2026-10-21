using Humans.Email.Contracts;
using AwesomeAssertions;
using Humans.MailerLite.Services.Audiences;
using Humans.MailerLite.Tests.Infrastructure;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Tests.Audiences;

public class MailerLiteAudienceBaseTests
{
    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_ExcludesExplicitMarketingOptOuts_KeepsNullAndOptIn()
    {
        var optedIn = Guid.NewGuid();   // Marketing OptedOut false → kept
        var noPref = Guid.NewGuid();    // no Marketing preference  → kept
        var optedOut = Guid.NewGuid();  // Marketing OptedOut true  → removed

        var audience = new FakeAudience(
            [optedIn, noPref, optedOut],
            MarketingPreferenceStubs.For((optedIn, false), (optedOut, true)));

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

        var audience = new FakeAudience(raw, MarketingPreferenceStubs.For((b, false)));

        var members = await audience.ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        members.Should().BeSameAs(raw,
            "with no opted-out users there is nothing to exclude, so the base returns the raw set");
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_EmptyRaw_DoesNotEnumerateUsers()
    {
        var prefs = Substitute.For<ICommunicationPreferenceService>();
        var audience = new FakeAudience([], prefs);

        var members = await audience.ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        members.Should().BeEmpty();
        prefs.ReceivedCalls().Should().BeEmpty();
    }

    private sealed class FakeAudience(HashSet<Guid> raw, ICommunicationPreferenceService prefs)
        : MailerLiteAudienceBase(Substitute.For<IUserService>(), prefs)
    {
        public override string Key => "fake";
        public override string DisplayName => "Fake";
        public override string MailerLiteGroupName => "Humans - Fake";

        protected override Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlySet<Guid>>(raw);
    }
}
