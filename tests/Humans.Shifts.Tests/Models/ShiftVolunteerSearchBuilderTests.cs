using Humans.Shifts.Domain;
using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Services;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using Humans.Shifts.Helpers;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Shifts.Tests.Models;

/// <summary>
/// <see cref="ShiftVolunteerSearchBuilder"/> juggles two burns that are easy to
/// conflate: the target shift's own burn (drives its absolute times, the pool
/// lookup and the signup filter) and the currently active burn (decides whether
/// the active-scoped cached <c>ShiftUserView.Signups</c> can be reused). Admins
/// search shifts in past/future cycles, so a single-burn fixture would pass even
/// if the two were collapsed (issue #809).
/// </summary>
public class ShiftVolunteerSearchBuilderTests
{
    private static readonly Guid ActiveBurnId = Guid.NewGuid();
    private static readonly Guid PastBurnId = Guid.NewGuid();

    private static readonly EventSettingsInfo ActiveBurn =
        MakeBurn(ActiveBurnId, "Europe/Madrid", new LocalDate(2026, 7, 9));

    private static readonly EventSettingsInfo PastBurn =
        MakeBurn(PastBurnId, "Europe/Madrid", new LocalDate(2025, 7, 9));

    private readonly ISettingsServiceRead _appSettings = Substitute.For<ISettingsServiceRead>();
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();
    private readonly IShiftRowView _shiftView = Substitute.For<IShiftRowView>();
    private readonly IShiftSignupService _signupService = Substitute.For<IShiftSignupService>();
    private readonly IVolunteerTrackingService _tracking = Substitute.For<IVolunteerTrackingService>();
    private readonly Guid _candidateId = Guid.NewGuid();

    public ShiftVolunteerSearchBuilderTests()
    {
        _appSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(ActiveBurn);
        _appSettings.GetEventSettingsByIdAsync(ActiveBurnId, Arg.Any<CancellationToken>()).Returns(ActiveBurn);
        _appSettings.GetEventSettingsByIdAsync(PastBurnId, Arg.Any<CancellationToken>()).Returns(PastBurn);

        _userService.SearchUsersAsync("ann", Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new HumanSearchResult(_candidateId, Guid.NewGuid(), "Ann", null, "Name", null, null, 100)]);
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>());
        _tracking.GetAvailableForDayAsync(Arg.Any<Guid>(), Arg.Any<int>()).Returns([]);
    }

    private ShiftVolunteerSearchBuilder BuildSut() =>
        new(_appSettings, _userService, _shiftView, _signupService, _tracking);

    [HumansFact]
    public async Task TargetInActiveBurn_ReusesCachedViewAndResolvesTheBurnOnce()
    {
        var target = MakeShift(ActiveBurnId, dayOffset: 1, new LocalTime(10, 0), Duration.FromHours(4));
        // Cached view is active-scoped, so it may be used as-is here.
        SetCachedSignups(MakeSignup(ActiveBurnId, dayOffset: 1, new LocalTime(12, 0), Duration.FromHours(4)));

        var result = await BuildSut().BuildForShiftAsync(target, "ann", canViewMedical: false);

        var row = Assert.Single(result.Results);
        Assert.Equal(1, row.BookedShiftCount);
        Assert.True(row.HasOverlap); // 12:00–16:00 overlaps the 10:00–14:00 target.

        // Active burn already in hand — no extra GetByIdAsync round trip.
        await _appSettings.DidNotReceiveWithAnyArgs().GetEventSettingsByIdAsync(default, default);
        await _signupService.DidNotReceiveWithAnyArgs().GetByUserAsync(default, default);
        await _tracking.Received(1).GetAvailableForDayAsync(ActiveBurnId, 1);
    }

    [HumansFact]
    public async Task TargetInAnotherBurn_UsesThatBurnsCalendarAndRefetchesSignups()
    {
        var target = MakeShift(PastBurnId, dayOffset: 1, new LocalTime(10, 0), Duration.FromHours(4));

        // The cached view holds the ACTIVE burn's signups at the same offsets/times.
        // If the builder reused them (or measured them against the active burn's
        // calendar) it would report a phantom overlap for a different year.
        SetCachedSignups(MakeSignup(ActiveBurnId, dayOffset: 1, new LocalTime(10, 0), Duration.FromHours(4)));
        _signupService.GetByUserAsync(_candidateId, PastBurnId).Returns(
        [
            MakeSignup(PastBurnId, dayOffset: 1, new LocalTime(20, 0), Duration.FromHours(2)),
        ]);

        var result = await BuildSut().BuildForShiftAsync(target, "ann", canViewMedical: false);

        var row = Assert.Single(result.Results);
        Assert.Equal(1, row.BookedShiftCount);
        Assert.False(row.HasOverlap); // 20:00–22:00 in the past burn misses 10:00–14:00.

        // Exactly one extra service call for the off-cycle burn — bounded, not per row.
        await _appSettings.Received(1).GetEventSettingsByIdAsync(PastBurnId, Arg.Any<CancellationToken>());
        await _signupService.Received(1).GetByUserAsync(_candidateId, PastBurnId);
        await _tracking.Received(1).GetAvailableForDayAsync(PastBurnId, 1);
    }

    [HumansFact]
    public async Task ShiftFromAnUnresolvableBurn_IsNotFound()
    {
        var unknownId = Guid.NewGuid();
        _appSettings.GetEventSettingsByIdAsync(unknownId, Arg.Any<CancellationToken>()).Returns((EventSettingsInfo?)null);

        var result = await BuildSut().BuildForShiftAsync(
            MakeShift(unknownId, dayOffset: 0, new LocalTime(9, 0), Duration.FromHours(2)),
            "ann",
            canViewMedical: false);

        Assert.Equal(VolunteerSearchBuildStatus.NotFound, result.Status);
    }

    [HumansFact]
    public async Task BlankQuery_ShortCircuitsBeforeAnyBurnLookup()
    {
        var result = await BuildSut().BuildForShiftAsync(
            MakeShift(ActiveBurnId, dayOffset: 0, new LocalTime(9, 0), Duration.FromHours(2)),
            "   ",
            canViewMedical: false);

        Assert.Equal(VolunteerSearchBuildStatus.EmptyQuery, result.Status);
        await _appSettings.DidNotReceiveWithAnyArgs().GetActiveEventSettingsAsync();
    }

    private void SetCachedSignups(params ShiftSignup[] signups) =>
        _shiftView.GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ShiftUserView>
            {
                [_candidateId] = new(_candidateId, null, null, null, [], signups),
            });

    private static Shift MakeShift(Guid burnId, int dayOffset, LocalTime start, Duration duration) =>
        new()
        {
            Id = Guid.NewGuid(),
            DayOffset = dayOffset,
            StartTime = start,
            Duration = duration,
            Rota = new Rota { Id = Guid.NewGuid(), EventSettingsId = burnId, Name = "Rota" },
        };

    private static ShiftSignup MakeSignup(Guid burnId, int dayOffset, LocalTime start, Duration duration) =>
        new()
        {
            Id = Guid.NewGuid(),
            Status = SignupStatus.Confirmed,
            Shift = MakeShift(burnId, dayOffset, start, duration),
        };

    private static EventSettingsInfo MakeBurn(Guid id, string timeZoneId, LocalDate gateOpening) =>
        new(
            Id: id,
            EventName: "Burn",
            Year: gateOpening.Year,
            TimeZoneId: timeZoneId,
            GateOpeningDate: gateOpening,
            BuildStartOffset: -7,
            EventEndOffset: 4,
            StrikeEndOffset: 6,
            FirstCrewStartOffset: -25,
            SetupWeekStartOffset: -16,
            PreEventWeekStartOffset: -9,
            FinishingWeekendStartOffset: -4,
            EarlyEntryCapacity: new Dictionary<int, int>(),
            BarriosEarlyEntryAllocation: null,
            EarlyEntryClose: null);
}
