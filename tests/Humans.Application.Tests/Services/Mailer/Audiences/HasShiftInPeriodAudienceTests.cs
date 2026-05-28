using AwesomeAssertions;
using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Mailer.Audiences;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NSubstitute;

namespace Humans.Application.Tests.Services.Mailer.Audiences;

public class HasShiftInPeriodAudienceTests
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

        var views = new Dictionary<Guid, ShiftUserView>
        {
            [build] = ViewWith(build, -3, SignupStatus.Confirmed),
            [@event] = ViewWith(@event, 2, SignupStatus.Confirmed),
            [strike] = ViewWith(strike, 8, SignupStatus.Confirmed),
            [bailed] = ViewWith(bailed, -3, SignupStatus.Bailed),
            [none] = ShiftUserView.Empty(none),
        };

        var members = await NewAudience<HasShiftSetupAudience>(views)
            .ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEquivalentTo([build]);
    }

    [HumansFact]
    public async Task EventAudience_ReturnsOnlyUsersWithActiveEventPeriodShift()
    {
        var build = Guid.NewGuid();
        var @event = Guid.NewGuid();
        var strike = Guid.NewGuid();

        var views = new Dictionary<Guid, ShiftUserView>
        {
            [build] = ViewWith(build, -3, SignupStatus.Pending),
            [@event] = ViewWith(@event, 2, SignupStatus.Pending),
            [strike] = ViewWith(strike, 8, SignupStatus.Pending),
        };

        var members = await NewAudience<HasShiftEventAudience>(views)
            .ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEquivalentTo([@event]);
    }

    [HumansFact]
    public async Task StrikeAudience_ReturnsOnlyUsersWithActiveStrikePeriodShift()
    {
        var build = Guid.NewGuid();
        var @event = Guid.NewGuid();
        var strike = Guid.NewGuid();

        var views = new Dictionary<Guid, ShiftUserView>
        {
            [build] = ViewWith(build, -3, SignupStatus.Confirmed),
            [@event] = ViewWith(@event, 5, SignupStatus.Confirmed),
            [strike] = ViewWith(strike, 8, SignupStatus.Confirmed),
        };

        var members = await NewAudience<HasShiftStrikeAudience>(views)
            .ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEquivalentTo([strike]);
    }

    [HumansFact]
    public void Metadata_KeysGroupNamesAndPrefix()
    {
        var empty = new Dictionary<Guid, ShiftUserView>();

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

    private static TAudience NewAudience<TAudience>(IReadOnlyDictionary<Guid, ShiftUserView> viewsByUser)
        where TAudience : HasShiftInPeriodAudienceBase
    {
        var users = Substitute.For<IUserService>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(viewsByUser.Keys
                .Select(id => new User { Id = id }.ToUserInfo())
                .ToList());

        var shiftView = Substitute.For<IShiftView>();
        shiftView.GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, ShiftUserView>>(viewsByUser));

        return (TAudience)Activator.CreateInstance(typeof(TAudience), shiftView, users)!;
    }

    private static ShiftUserView ViewWith(Guid userId, int dayOffset, SignupStatus status)
    {
        var eventSettings = new EventSettings { Id = Guid.NewGuid(), EventEndOffset = EventEndOffset };
        return new ShiftUserView(
            UserId: userId,
            Profile: null,
            Availability: null,
            BuildStatus: null,
            TagPreferences: [],
            Signups: [new ShiftSignup
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShiftId = Guid.NewGuid(),
                Status = status,
                Shift = new Shift
                {
                    DayOffset = dayOffset,
                    Rota = new Rota { EventSettings = eventSettings },
                },
            }]);
    }
}
