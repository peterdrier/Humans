using AwesomeAssertions;
using Humans.Cantina.Contracts;
using Humans.Shifts.Domain;
using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Shifts.Tests.Infrastructure;
using Humans.Users.Contracts;
using Humans.Users.Domain;
using Humans.Shifts.Helpers;
using Humans.Shifts.Models;
using Humans.Settings.Contracts;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Shifts.Tests.Models;

/// <summary>
/// Two burns are easy to conflate here: the target shift's own (its times, the pool and the
/// signup filter) and the active one (whether the active-scoped cached signups can be reused).
/// Admins search past and future cycles, so a single-burn fixture would hide a mix-up.
/// </summary>
public class ShiftVolunteerSearchBuilderTests
{
    private static readonly Guid ActiveBurnId = Guid.NewGuid();
    private static readonly Guid PastBurnId = Guid.NewGuid();

    private static readonly BurnSettingsInfo ActiveBurn =
        MakeBurn(ActiveBurnId, "Europe/Madrid", new LocalDate(2026, 7, 9));

    private static readonly BurnSettingsInfo PastBurn =
        MakeBurn(PastBurnId, "Europe/Madrid", new LocalDate(2025, 7, 9));

    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ICantinaService _cantina = Substitute.For<ICantinaService>();
    private readonly IShiftRowView _shiftView = Substitute.For<IShiftRowView>();
    private readonly IShiftSignupService _signupService = Substitute.For<IShiftSignupService>();
    private readonly IVolunteerTrackingManagement _tracking = Substitute.For<IVolunteerTrackingManagement>();
    private readonly Guid _candidateId = Guid.NewGuid();

    public ShiftVolunteerSearchBuilderTests()
    {
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(ActiveBurn);
        _burnSettings.GetByIdAsync(ActiveBurnId, Arg.Any<CancellationToken>()).Returns(ActiveBurn);
        _burnSettings.GetByIdAsync(PastBurnId, Arg.Any<CancellationToken>()).Returns(PastBurn);

        _userService.SearchUsersAsync("ann", Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new HumanSearchResult(_candidateId, Guid.NewGuid(), "Ann", null, "Name", null, null, 100)]);
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>());
        _tracking.GetAvailableForDayAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _cantina.GetDietaryProfilesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, DietaryProfileInfo>());
    }

    private ShiftVolunteerSearchBuilder BuildSut() =>
        new(_burnSettings, _userService, _cantina, _shiftView, _signupService, _tracking);

    [HumansFact]
    public async Task SearchReads_PreserveCancellationAcrossEventAndCandidateLookups()
    {
        using var cancellation = new CancellationTokenSource();
        var target = MakeShift(PastBurnId, 1, new LocalTime(10, 0), Duration.FromHours(4));
        SetCachedSignups();
        _signupService.GetByUserAsync(_candidateId, PastBurnId, Arg.Any<CancellationToken>()).Returns([]);

        var result = await BuildSut().BuildForShiftAsync(target, "ann", cancellation.Token);

        Assert.Single(result.Results);
        await _burnSettings.Received(1).GetActiveAsync(cancellation.Token);
        await _burnSettings.Received(1).GetByIdAsync(PastBurnId, cancellation.Token);
        await _userService.Received(1).SearchUsersAsync("ann", PersonSearchFields.Name, int.MaxValue, cancellation.Token);
        await _tracking.Received(1).GetAvailableForDayAsync(PastBurnId, 1, cancellation.Token);
        await _shiftView.Received(1).GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), cancellation.Token);
        await _signupService.Received(1).GetByUserAsync(_candidateId, PastBurnId, cancellation.Token);
    }

    [HumansFact]
    public async Task TargetInActiveBurn_ReusesCachedViewAndResolvesTheBurnOnce()
    {
        var target = MakeShift(ActiveBurnId, dayOffset: 1, new LocalTime(10, 0), Duration.FromHours(4));
        // Cached view is active-scoped, so it may be used as-is here.
        SetCachedSignups(MakeSignup(ActiveBurnId, dayOffset: 1, new LocalTime(12, 0), Duration.FromHours(4)));

        var result = await BuildSut().BuildForShiftAsync(target, "ann");

        var row = Assert.Single(result.Results);
        Assert.Equal(1, row.BookedShiftCount);
        Assert.True(row.HasOverlap); // 12:00–16:00 overlaps the 10:00–14:00 target.

        // Active burn already in hand — no extra GetByIdAsync round trip.
        await _burnSettings.DidNotReceiveWithAnyArgs().GetByIdAsync(default);
        await _signupService.DidNotReceiveWithAnyArgs().GetByUserAsync(default);
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
            ShiftUserSummaryProjection.ToSummary(
                MakeSignup(PastBurnId, dayOffset: 1, new LocalTime(20, 0), Duration.FromHours(2)),
                ToCalendar(PastBurn)),
        ]);

        var result = await BuildSut().BuildForShiftAsync(target, "ann");

        var row = Assert.Single(result.Results);
        Assert.Equal(1, row.BookedShiftCount);
        Assert.False(row.HasOverlap); // 20:00–22:00 in the past burn misses 10:00–14:00.

        // Exactly one extra service call for the off-cycle burn — bounded, not per row.
        await _burnSettings.Received(1).GetByIdAsync(PastBurnId, Arg.Any<CancellationToken>());
        await _signupService.Received(1).GetByUserAsync(_candidateId, PastBurnId);
        await _tracking.Received(1).GetAvailableForDayAsync(PastBurnId, 1);
    }

    [HumansFact]
    public async Task ShiftFromAnUnresolvableBurn_IsNotFound()
    {
        var unknownId = Guid.NewGuid();
        _burnSettings.GetByIdAsync(unknownId, Arg.Any<CancellationToken>()).Returns((BurnSettingsInfo?)null);

        var result = await BuildSut().BuildForShiftAsync(
            MakeShift(unknownId, dayOffset: 0, new LocalTime(9, 0), Duration.FromHours(2)),
            "ann");

        Assert.Equal(VolunteerSearchBuildStatus.NotFound, result.Status);
    }

    [HumansFact]
    public void SearchRows_NeverCarryMedicalConditions()
    {
        // Art. 9 data leaves the server only through ShiftsController.RevealMedical, one audited person at a time.
        typeof(VolunteerSearchResult).GetProperties().Select(p => p.Name)
            .Should().NotContain(name => name.Contains("Medical", StringComparison.Ordinal));
        typeof(VolunteerBadgesViewModel).GetProperties()
            .Where(p => p.PropertyType == typeof(string)).Select(p => p.Name)
            .Should().Equal(["DietaryPreference"], "the badge model carries a medical flag, never the text");
    }

    [HumansFact]
    public async Task DietaryBadge_ComesFromCantina()
    {
        var target = MakeShift(ActiveBurnId, dayOffset: 1, new LocalTime(10, 0), Duration.FromHours(4));
        SetCachedSignups();
        _cantina.GetDietaryProfilesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, DietaryProfileInfo>
            {
                [_candidateId] = new(_candidateId, "Vegan", [], null, [], null),
            });

        var result = await BuildSut().BuildForShiftAsync(target, "ann");

        Assert.Equal("Vegan", Assert.Single(result.Results).DietaryPreference);
    }

    [HumansFact]
    public async Task BlankQuery_ShortCircuitsBeforeAnyBurnLookup()
    {
        var result = await BuildSut().BuildForShiftAsync(
            MakeShift(ActiveBurnId, dayOffset: 0, new LocalTime(9, 0), Duration.FromHours(2)),
            "   ");

        Assert.Equal(VolunteerSearchBuildStatus.EmptyQuery, result.Status);
        await _burnSettings.DidNotReceiveWithAnyArgs().GetActiveAsync();
    }

    private void SetCachedSignups(params ShiftSignup[] signups) =>
        _shiftView.GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ShiftUserView>
            {
                [_candidateId] = new(_candidateId, null, null, null, [], signups, ToCalendar(ActiveBurn)),
            });

    // The target as GetShiftByIdAsync hands it over: the shift's own fields plus its rota.
    private static ShiftWithRota MakeShift(Guid burnId, int dayOffset, LocalTime start, Duration duration)
    {
        var rotaId = Guid.NewGuid();
        return new ShiftWithRota(
            new ShiftInfo(Guid.NewGuid(), rotaId, null, dayOffset, start, duration.TotalHours, 0, 5, false, false, dayOffset < 0),
            new RotaInfo(rotaId, burnId, Guid.NewGuid(), "Rota", null, null,
                ShiftPriority.Normal, SignupPolicy.Public, RotaPeriod.Event, true, []));
    }

    private static Shift MakeShiftEntity(Guid burnId, int dayOffset, LocalTime start, Duration duration) =>
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
            Shift = MakeShiftEntity(burnId, dayOffset, start, duration),
        };

    private static EventSettingsInfo ToCalendar(BurnSettingsInfo burn) => new(
        Id: burn.Id,
        EventName: burn.EventName,
        Year: burn.Year,
        TimeZoneId: burn.TimeZoneId,
        GateOpeningDate: burn.GateOpeningDate,
        BuildStartOffset: burn.BuildStartOffset,
        EventEndOffset: burn.EventEndOffset,
        StrikeEndOffset: burn.StrikeEndOffset,
        FirstCrewStartOffset: burn.FirstCrewStartOffset,
        SetupWeekStartOffset: burn.SetupWeekStartOffset,
        PreEventWeekStartOffset: burn.PreEventWeekStartOffset,
        FinishingWeekendStartOffset: burn.FinishingWeekendStartOffset,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null);

    private static BurnSettingsInfo MakeBurn(Guid id, string timeZoneId, LocalDate gateOpening) =>
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
            EarlyEntryClose: null,
            IsShiftBrowsingOpen: true);
}
