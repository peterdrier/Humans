using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.MailerLite.Tests.Infrastructure;
using Humans.MailerLite.Services.Audiences;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Tests.Audiences;

public class ShiftViewAudienceTests
{
    private const int EventEndOffset = 5;

    [HumansFact]
    public async Task SetupAudience_ReturnsOnlyUsersWithActiveBuildPeriodShift()
    {
        var build = Guid.NewGuid();   // DayOffset -3, Confirmed → IN
        var @event = Guid.NewGuid();  // DayOffset  2, Confirmed → OUT
        var strike = Guid.NewGuid();  // DayOffset  8, Confirmed → OUT
        var bailed = Guid.NewGuid();  // DayOffset -3, Bailed    → OUT
        var none = Guid.NewGuid();    // no signups              → OUT

        var views = new Dictionary<Guid, ShiftUserSummary>
        {
            [build] = ViewWith(build, -3, SignupStatus.Confirmed),
            [@event] = ViewWith(@event, 2, SignupStatus.Confirmed),
            [strike] = ViewWith(strike, 8, SignupStatus.Confirmed),
            [bailed] = ViewWith(bailed, -3, SignupStatus.Bailed),
            [none] = ShiftUserSummary.Empty(none),
        };

        var members = await NewAudience<HasShiftSetupAudience>(views)
            .ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        members.Should().BeEquivalentTo([build]);
    }

    [HumansFact]
    public async Task EventAudience_ReturnsOnlyUsersWithActiveEventPeriodShift()
    {
        var build = Guid.NewGuid();
        var @event = Guid.NewGuid();
        var strike = Guid.NewGuid();

        var views = new Dictionary<Guid, ShiftUserSummary>
        {
            [build] = ViewWith(build, -3, SignupStatus.Pending),
            [@event] = ViewWith(@event, 2, SignupStatus.Pending),
            [strike] = ViewWith(strike, 8, SignupStatus.Pending),
        };

        var members = await NewAudience<HasShiftEventAudience>(views)
            .ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        members.Should().BeEquivalentTo([@event]);
    }

    [HumansFact]
    public async Task StrikeAudience_ReturnsOnlyUsersWithActiveStrikePeriodShift()
    {
        var build = Guid.NewGuid();
        var @event = Guid.NewGuid();
        var strike = Guid.NewGuid();

        var views = new Dictionary<Guid, ShiftUserSummary>
        {
            [build] = ViewWith(build, -3, SignupStatus.Confirmed),
            [@event] = ViewWith(@event, 5, SignupStatus.Confirmed),
            [strike] = ViewWith(strike, 8, SignupStatus.Confirmed),
        };

        var members = await NewAudience<HasShiftStrikeAudience>(views)
            .ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        members.Should().BeEquivalentTo([strike]);
    }

    [HumansFact]
    public void Metadata_KeysGroupNamesAndPrefix()
    {
        var empty = new Dictionary<Guid, ShiftUserSummary>();

        var setup = NewAudience<HasShiftSetupAudience>(empty);
        setup.Key.Should().Be("has-shift-setup");
        setup.MailerLiteGroupName.Should().Be("Humans - Has Shift - Setup");

        var @event = NewAudience<HasShiftEventAudience>(empty);
        @event.Key.Should().Be("has-shift-event");
        @event.MailerLiteGroupName.Should().Be("Humans - Has Shift - Event");

        var strike = NewAudience<HasShiftStrikeAudience>(empty);
        strike.Key.Should().Be("has-shift-strike");
        strike.MailerLiteGroupName.Should().Be("Humans - Has Shift - Strike");
    }

    private static TAudience NewAudience<TAudience>(IReadOnlyDictionary<Guid, ShiftUserSummary> viewsByUser)
        where TAudience : ShiftViewAudienceBase
    {
        var users = Substitute.For<IUserService>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(viewsByUser.Keys
                .Select(id => new User { Id = id }.ToUserInfo())
                .ToList());

        var shiftView = Substitute.For<IShiftView>();
        shiftView.GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, ShiftUserSummary>>(viewsByUser));

        return (TAudience)Activator.CreateInstance(typeof(TAudience), shiftView, users)!;
    }

    /// <summary>
    /// One signup in the period <paramref name="dayOffset"/> falls in — the
    /// section resolves the period against the event's offsets before the
    /// summary crosses the boundary, so the test states it directly.
    /// </summary>
    private static ShiftUserSummary ViewWith(Guid userId, int dayOffset, SignupStatus status)
    {
        var period =
            dayOffset < 0 ? ShiftPeriod.Build :
            dayOffset <= EventEndOffset ? ShiftPeriod.Event :
            ShiftPeriod.Strike;
        return ShiftFixtures.UserSummary(
            userId,
            [ShiftFixtures.Signup(status: status, period: period)]);
    }
}
