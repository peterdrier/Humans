using AwesomeAssertions;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Shifts.Models;
using NodaTime;
using NodaTime.Text;
using NSubstitute;

namespace Humans.Shifts.Tests.Models;

/// <summary>
/// A single-day selection wins over the phase/date-range filters and forces the flat
/// openings-ranked view.
/// </summary>
public class ShiftBrowsePageBuilderDayFilterTests
{
    private readonly IShiftManagement _shiftManagement = Substitute.For<IShiftManagement>();
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();

    private static readonly BurnSettingsInfo Event = new(
        Id: Guid.NewGuid(),
        EventName: "Test Event 2026",
        Year: 2026,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new LocalDate(2026, 7, 1),
        BuildStartOffset: -14,
        EventEndOffset: 6,
        StrikeEndOffset: 9,
        FirstCrewStartOffset: -14,
        SetupWeekStartOffset: -10,
        PreEventWeekStartOffset: -7,
        FinishingWeekendStartOffset: -3,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        IsShiftBrowsingOpen: true);

    private ShiftBrowsePageBuilder CreateBuilder()
    {
        _shiftManagement.GetTagsAsync(ct: Arg.Any<CancellationToken>()).Returns(Array.Empty<ShiftTagSummary>());
        _shiftManagement.GetDepartmentCoveragePiesAsync(
                Arg.Any<Guid>(), Arg.Any<LocalDate?>(), Arg.Any<LocalDate?>(), ct: Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DepartmentCoveragePie>());
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
        return new ShiftBrowsePageBuilder(_shiftManagement, _burnSettings, _teamService);
    }

    private static ShiftBrowsePageRequest RequestFor(string? day, string? sort = null) => new(
        Event,
        Guid.NewGuid(),
        [],
        [],
        [],
        DepartmentId: null,
        FromDate: null,
        ToDate: null,
        Period: null,
        Day: day,
        ShowFull: false,
        TagIds: null,
        Sort: sort,
        Periods: null,
        IsPrivileged: false);

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task BuildAsync_PreservesCancellationInTeamAndTagReads(bool tags)
    {
        var builder = CreateBuilder();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var ct = cancellation.Token;
        var failure = new OperationCanceledException(ct);
        _shiftManagement.GetBrowseShiftsAsync(Arg.Any<ShiftBrowseQuery>(), ct)
            .Returns(Array.Empty<UrgentShiftInfo>());
        if (tags)
            _shiftManagement.GetTagsAsync(ct: ct)
                .Returns(Task.FromException<IReadOnlyList<ShiftTagSummary>>(failure));
        else
            _teamService.GetTeamsAsync(ct)
                .Returns(Task.FromException<IReadOnlyDictionary<Guid, TeamInfo>>(failure));

        var act = () => builder.BuildAsync(RequestFor(day: null), ct);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(failure);
    }

    [HumansFact]
    public async Task DaySelected_RanksRotasByRemainingOpenings_FullRotaLast()
    {
        var teamId = Guid.NewGuid();
        var day = Event.GateOpeningDate.PlusDays(3);

        var openShift = UrgentShiftFixtures.Shift(rotaId: Guid.NewGuid(), dayOffset: 3, maxVolunteers: 5);
        var openRota = UrgentShiftFixtures.Rota(id: openShift.RotaId, name: "Open Rota", teamId: teamId);
        var openUrgent = UrgentShiftFixtures.Urgent(
            shift: openShift, rota: openRota, burn: Event, confirmedCount: 1, remainingSlots: 4);

        var fullShift = UrgentShiftFixtures.Shift(rotaId: Guid.NewGuid(), dayOffset: 3, maxVolunteers: 2);
        var fullRota = UrgentShiftFixtures.Rota(id: fullShift.RotaId, name: "Full Rota", teamId: teamId);
        var fullUrgent = UrgentShiftFixtures.Urgent(
            shift: fullShift, rota: fullRota, burn: Event, confirmedCount: 2, remainingSlots: 0);

        _shiftManagement.GetBrowseShiftsAsync(Arg.Any<ShiftBrowseQuery>(), Arg.Any<CancellationToken>())
            .Returns([openUrgent, fullUrgent]);

        var builder = CreateBuilder();
        // Deliberately request department sort — the day filter must override it.
        var request = RequestFor(LocalDatePattern.Iso.Format(day), sort: "department");

        var model = await builder.BuildAsync(request, Xunit.TestContext.Current.CancellationToken);

        model.Sort.Should().Be("urgency");
        model.FilterDay.Should().Be(LocalDatePattern.Iso.Format(day));
        model.UrgencyRankedRotas.Select(r => r.Rota.Name).Should().Equal("Open Rota", "Full Rota");
    }

    [HumansFact]
    public async Task NoDaySelected_FilterDayIsNull_AndExistingSortIsHonored()
    {
        _shiftManagement.GetBrowseShiftsAsync(Arg.Any<ShiftBrowseQuery>(), Arg.Any<CancellationToken>()).Returns([]);
        var builder = CreateBuilder();

        var model = await builder.BuildAsync(RequestFor(day: null, sort: "department"), Xunit.TestContext.Current.CancellationToken);

        model.FilterDay.Should().BeNull();
        model.Sort.Should().Be("department");
    }

    [HumansFact]
    public async Task DayOptions_GroupByCalendarDay_AndCarryPeriodAndSubPeriod()
    {
        // Build-period day inside the PreEventWeek window (-7 <= -5 < -3).
        var buildShift = UrgentShiftFixtures.Shift(rotaId: Guid.NewGuid(), dayOffset: -5, isAllDay: true);
        var buildUrgent = UrgentShiftFixtures.Urgent(shift: buildShift, burn: Event);

        // Event-period day; a second shift the same day should collapse to one option.
        var eventShift1 = UrgentShiftFixtures.Shift(rotaId: Guid.NewGuid(), dayOffset: 3);
        var eventUrgent1 = UrgentShiftFixtures.Urgent(shift: eventShift1, burn: Event);
        var eventShift2 = UrgentShiftFixtures.Shift(rotaId: Guid.NewGuid(), dayOffset: 3);
        var eventUrgent2 = UrgentShiftFixtures.Urgent(shift: eventShift2, burn: Event);

        _shiftManagement.GetBrowseShiftsAsync(Arg.Any<ShiftBrowseQuery>(), Arg.Any<CancellationToken>())
            .Returns([buildUrgent, eventUrgent1, eventUrgent2]);

        var builder = CreateBuilder();
        var model = await builder.BuildAsync(RequestFor(day: null), Xunit.TestContext.Current.CancellationToken);

        model.DayOptions.Should().HaveCount(2);

        var buildOption = model.DayOptions.Single(o => o.Date == Event.GateOpeningDate.PlusDays(-5));
        buildOption.Period.Should().Be(ShiftPeriod.Build);
        buildOption.SubPeriod.Should().Be(BuildSubPeriod.PreEventWeek);

        var eventOption = model.DayOptions.Single(o => o.Date == Event.GateOpeningDate.PlusDays(3));
        eventOption.Period.Should().Be(ShiftPeriod.Event);
        eventOption.SubPeriod.Should().BeNull();
    }
}
