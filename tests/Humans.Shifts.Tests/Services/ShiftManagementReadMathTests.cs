using Humans.Shifts.Domain;
using Humans.Auth.Contracts;
using Humans.Teams.Domain;
using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Tests.Infrastructure;
using Humans.Shifts.Data;
using NodaTime;
using NSubstitute;
using Xunit;
using Humans.Users.Contracts;

namespace Humans.Shifts.Tests.Services;

/// <summary>
/// Aggregation arithmetic on the ShiftManagementService read paths: the per-day
/// staffing snapshot, the department shift summary, and urgent-shift filtering.
/// </summary>
public sealed class ShiftManagementReadMathTests : ShiftsTestHarness
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);
    private static readonly LocalDate GateOpening = new(2026, 6, 10);

    private readonly ShiftManagementService _service;

    public ShiftManagementReadMathTests()
        : base(TestNow)
    {
        var teamService = Substitute.For<ITeamServiceRead>();
        var userService = Substitute.For<IUserService>();
        userService.StubGetUserInfosFromContext(Db);

        teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(
                TeamsDb.Teams.AsEnumerable().ToDictionary(t => t.Id, ToTeamInfo)));
        teamService.GetTeamAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.Arg<Guid>();
                var team = TeamsDb.Teams.AsEnumerable().FirstOrDefault(t => t.Id == id);
                return Task.FromResult(team is null ? null : ToTeamInfo(team));
            });

        var serviceProvider = new ServiceLocatorBuilder()
            .With(teamService)
            .With(userService)
            .With<IUserServiceRead>(userService)
            .With(Substitute.For<IRoleAssignmentService>())
            .Build();

        _service = new ShiftManagementService(
            new ShiftRepository(ShiftsDbFactory, ShiftsDb, Clock),
            AuditLog,
            AdminAuthorization,
            serviceProvider,
            Cache,
            Substitute.For<IShiftViewInvalidator>(),
            Clock);
    }

    // ============================================================
    // GetStaffingSnapshotAsync
    // ============================================================

    [HumansFact]
    public async Task GetStaffingSnapshot_ReturnsEmpty_WhenEventMissing()
    {
        var snapshot = await _service.GetStaffingSnapshotAsync(Guid.NewGuid());

        snapshot.Should().BeSameAs(ShiftStaffingSnapshot.Empty);
    }

    [HumansFact]
    public async Task GetStaffingSnapshot_CountsAShiftOnItsOwnDayOnly()
    {
        var (es, rota) = SeedScenario();
        SeedShift(rota, dayOffset: 2, min: 1, max: 4);
        await SaveAllAsync(Ct);

        var snapshot = await _service.GetStaffingSnapshotAsync(es.Id);

        snapshot.StaffingData.Where(d => d.TotalSlots > 0)
            .Select(d => d.DayOffset).Should().Equal(2);
        snapshot.StaffingData.Single(d => d.DayOffset == 1).TotalSlots.Should().Be(0);
        snapshot.StaffingData.Single(d => d.DayOffset == 3).TotalSlots.Should().Be(0);
    }

    [HumansFact]
    public async Task GetStaffingSnapshot_SumsSlotsAndConfirmedAcrossEveryShiftOnTheDay()
    {
        var (es, rota) = SeedScenario();
        var morning = SeedShift(rota, dayOffset: 2, min: 1, max: 4);
        var evening = SeedShift(rota, dayOffset: 2, min: 3, max: 7, startHour: 14);
        SeedSignup(morning, SeedUser("Alice").Id, SignupStatus.Confirmed);
        SeedSignup(morning, SeedUser("Bob").Id, SignupStatus.Pending);
        SeedSignup(evening, SeedUser("Carol").Id, SignupStatus.Confirmed);
        await SaveAllAsync(Ct);

        var day = (await _service.GetStaffingSnapshotAsync(es.Id))
            .StaffingData.Single(d => d.DayOffset == 2);

        day.TotalSlots.Should().Be(11);
        day.MinSlots.Should().Be(4);
        day.ConfirmedCount.Should().Be(2);
    }

    [HumansTheory]
    [InlineData(-1, "Set-up")]
    [InlineData(0, "Event")]
    [InlineData(6, "Event")]
    [InlineData(7, "Strike")]
    public async Task GetStaffingSnapshot_LabelsEachDayByItsPeriod(int dayOffset, string expected)
    {
        var (es, _) = SeedScenario();
        await SaveAllAsync(Ct);

        var snapshot = await _service.GetStaffingSnapshotAsync(es.Id);

        snapshot.StaffingData.Single(d => d.DayOffset == dayOffset).Period.Should().Be(expected);
    }

    [HumansFact]
    public async Task GetStaffingSnapshot_ScalesHoursByMaxVolunteers_AndBucketsThemByRotaPriority()
    {
        var (es, essentialRota) = SeedScenario(ShiftPriority.Essential);
        var importantRota = SeedRota(es, essentialRota.TeamId, ShiftPriority.Important);
        var normalRota = SeedRota(es, essentialRota.TeamId, ShiftPriority.Normal);
        SeedShift(essentialRota, dayOffset: 2, min: 1, max: 3, durationHours: 4);
        SeedShift(importantRota, dayOffset: 2, min: 1, max: 2, durationHours: 5, startHour: 13);
        SeedShift(normalRota, dayOffset: 2, min: 1, max: 1, durationHours: 6, startHour: 19);
        await SaveAllAsync(Ct);

        var hours = (await _service.GetStaffingSnapshotAsync(es.Id))
            .StaffingHours.Single(h => h.DayOffset == 2);

        hours.EssentialHours.Should().Be(12);
        hours.ImportantHours.Should().Be(10);
        hours.NormalHours.Should().Be(6);
    }

    [HumansFact]
    public async Task GetStaffingSnapshot_UsesTheAllDayWindow_NotTheDurationColumn()
    {
        var (es, rota) = SeedScenario();
        var shift = SeedShift(rota, dayOffset: -2, min: 1, max: 2, durationHours: 24);
        shift.IsAllDay = true;
        shift.StartTime = LocalTime.Midnight;
        await SaveAllAsync(Ct);

        var hours = (await _service.GetStaffingSnapshotAsync(es.Id))
            .StaffingHours.Single(h => h.DayOffset == -2);

        hours.NormalHours.Should().Be(Shift.AllDayWindowHours * 2);
    }

    // ============================================================
    // GetShiftsSummaryAsync
    // ============================================================

    [HumansFact]
    public async Task GetShiftsSummary_ReturnsNull_WhenNoTeamsRequested()
    {
        (await _service.GetShiftsSummaryAsync(Guid.NewGuid(), [])).Should().BeNull();
    }

    [HumansFact]
    public async Task GetShiftsSummary_ReturnsNull_WhenTheTeamsRotasHaveNoShifts()
    {
        var (es, rota) = SeedScenario();
        await SaveAllAsync(Ct);

        (await _service.GetShiftsSummaryAsync(es.Id, [rota.TeamId])).Should().BeNull();
    }

    [HumansFact]
    public async Task GetShiftsSummary_SumsSlotsAndCountsOnlyConfirmedSignups()
    {
        var (es, rota) = SeedScenario();
        var first = SeedShift(rota, dayOffset: 1, min: 1, max: 3);
        var second = SeedShift(rota, dayOffset: 2, min: 1, max: 4);
        var alice = SeedUser("Alice");
        SeedSignup(first, alice.Id, SignupStatus.Confirmed);
        SeedSignup(second, alice.Id, SignupStatus.Confirmed);
        SeedSignup(second, SeedUser("Bob").Id, SignupStatus.Bailed);
        await SaveAllAsync(Ct);

        var summary = await _service.GetShiftsSummaryAsync(es.Id, [rota.TeamId]);

        summary!.TotalSlots.Should().Be(7);
        summary.ConfirmedCount.Should().Be(2);
        summary.UniqueVolunteerCount.Should().Be(1);
    }

    [HumansFact]
    public async Task GetShiftsSummary_CountsARangeSignupBlockOnce()
    {
        var (es, rota) = SeedScenario();
        var first = SeedShift(rota, dayOffset: 1, min: 1, max: 3);
        var second = SeedShift(rota, dayOffset: 2, min: 1, max: 3);
        var third = SeedShift(rota, dayOffset: 3, min: 1, max: 3);
        var blockId = Guid.NewGuid();
        var alice = SeedUser("Alice");
        SeedSignup(first, alice.Id, SignupStatus.Pending, blockId);
        SeedSignup(second, alice.Id, SignupStatus.Pending, blockId);
        SeedSignup(third, SeedUser("Bob").Id, SignupStatus.Pending);
        await SaveAllAsync(Ct);

        var summary = await _service.GetShiftsSummaryAsync(es.Id, [rota.TeamId]);

        summary!.PendingCount.Should().Be(2);
    }

    [HumansFact]
    public async Task GetShiftsSummary_ReportsOnlyTeamsThatActuallyOwnShifts()
    {
        var (es, rota) = SeedScenario();
        var otherTeam = SeedTeam("Other Department");
        var emptyRota = SeedRota(es, otherTeam.Id, ShiftPriority.Normal);
        SeedShift(rota, dayOffset: 1, min: 1, max: 3);
        await SaveAllAsync(Ct);

        var summary = await _service.GetShiftsSummaryAsync(es.Id, [rota.TeamId, emptyRota.TeamId]);

        summary!.TeamIdsWithShifts.Should().BeEquivalentTo([rota.TeamId]);
    }

    // ============================================================
    // GetUrgentShiftsAsync
    // ============================================================

    [HumansFact]
    public async Task GetUrgentShifts_ExcludesShiftsThatHaveAlreadyEnded()
    {
        var (es, rota) = SeedScenario();
        SeedShift(rota, dayOffset: 4, min: 1, max: 3);
        var future = SeedShift(rota, dayOffset: 8, min: 1, max: 3);
        await SaveAllAsync(Ct);

        var urgent = await _service.GetUrgentShiftsAsync(es.Id);

        urgent.Select(u => u.Shift.Id).Should().Equal(future.Id);
    }

    [HumansFact]
    public async Task GetUrgentShifts_ExcludesFullShiftsAndClampsRemainingAtZero()
    {
        var (es, rota) = SeedScenario();
        var full = SeedShift(rota, dayOffset: 8, min: 1, max: 1);
        var open = SeedShift(rota, dayOffset: 8, min: 1, max: 2, startHour: 14);
        SeedSignup(full, SeedUser("Alice").Id, SignupStatus.Confirmed);
        SeedSignup(full, SeedUser("Bob").Id, SignupStatus.Confirmed);
        await SaveAllAsync(Ct);

        var urgent = await _service.GetUrgentShiftsAsync(es.Id);

        urgent.Select(u => u.Shift.Id).Should().Equal(open.Id);
        urgent.Single().RemainingSlots.Should().Be(2);
    }

    [HumansFact]
    public async Task GetUrgentShifts_SwapsAnInvertedDateRange()
    {
        var (es, rota) = SeedScenario();
        var early = SeedShift(rota, dayOffset: 6, min: 1, max: 3);
        var late = SeedShift(rota, dayOffset: 8, min: 1, max: 3);
        await SaveAllAsync(Ct);

        var urgent = await _service.GetUrgentShiftsAsync(
            es.Id,
            startDate: GateOpening.PlusDays(8),
            endDate: GateOpening.PlusDays(6));

        urgent.Select(u => u.Shift.Id).Should().BeEquivalentTo([early.Id, late.Id]);
    }

    [HumansFact]
    public async Task GetUrgentShifts_TreatsAStartOnlyBoundAsAnOpenEndedRange()
    {
        var (es, rota) = SeedScenario();
        SeedShift(rota, dayOffset: 6, min: 1, max: 3);
        var onBound = SeedShift(rota, dayOffset: 8, min: 1, max: 3);
        var afterBound = SeedShift(rota, dayOffset: 9, min: 1, max: 3);
        await SaveAllAsync(Ct);

        var urgent = await _service.GetUrgentShiftsAsync(es.Id, startDate: GateOpening.PlusDays(8));

        urgent.Select(u => u.Shift.Id).Should().BeEquivalentTo([onBound.Id, afterBound.Id]);
    }

    [HumansFact]
    public async Task GetUrgentShifts_TreatsAnEndOnlyBoundAsAnOpenEndedRange()
    {
        var (es, rota) = SeedScenario();
        var beforeBound = SeedShift(rota, dayOffset: 6, min: 1, max: 3);
        var onBound = SeedShift(rota, dayOffset: 8, min: 1, max: 3);
        SeedShift(rota, dayOffset: 9, min: 1, max: 3);
        await SaveAllAsync(Ct);

        var urgent = await _service.GetUrgentShiftsAsync(es.Id, endDate: GateOpening.PlusDays(8));

        urgent.Select(u => u.Shift.Id).Should().BeEquivalentTo([beforeBound.Id, onBound.Id]);
    }

    // ============================================================
    // ApplyPeriodDiverseLimit
    // ============================================================

    [HumansFact]
    public void ApplyPeriodDiverseLimit_ReturnsTheListUntouched_WhenCountEqualsLimit()
    {
        var es = NewEventSettings();
        var ranked = new List<UrgentShiftInfo>
        {
            NewUrgentShift(es, dayOffset: 1, score: 5),
            NewUrgentShift(es, dayOffset: 8, score: 9)
        };

        var result = ShiftManagementService.ApplyPeriodDiverseLimit(ranked, 2);

        result.Should().BeSameAs(ranked);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static TeamInfo ToTeamInfo(Team team) =>
        new(
            team.Id, team.Name, team.Description, team.Slug,
            team.IsActive, team.IsSystemTeam, team.SystemTeamType, team.RequiresApproval,
            team.IsPublicPage, team.IsHidden, team.IsPromotedToDirectory, team.CreatedAt,
            Members: [],
            ParentTeamId: team.ParentTeamId);

    private static EventSettings NewEventSettings() =>
        new()
        {
            Id = Guid.NewGuid(),
            EventName = "Test Event 2026",
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = GateOpening,
            BuildStartOffset = -5,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsShiftBrowsingOpen = true,
            IsActive = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };

    private static UrgentShiftInfo NewUrgentShift(EventSettings es, int dayOffset, double score) =>
        UrgentShiftFixtures.Urgent(
            shift: UrgentShiftFixtures.Shift(dayOffset: dayOffset),
            burn: es,
            urgencyScore: score,
            remainingSlots: 3);

    private (EventSettings Es, Rota Rota) SeedScenario(ShiftPriority priority = ShiftPriority.Normal)
    {
        var es = NewEventSettings();
        ShiftsDb.EventSettings.Add(es);
        var team = SeedTeam("Test Department");
        return (es, SeedRota(es, team.Id, priority));
    }

    private Rota SeedRota(EventSettings es, Guid teamId, ShiftPriority priority)
    {
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = teamId,
            Name = $"Rota {priority}",
            Priority = priority,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.All,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            EventSettings = es
        };
        ShiftsDb.Rotas.Add(rota);
        return rota;
    }

    private Shift SeedShift(
        Rota rota, int dayOffset, int min, int max, double durationHours = 4, int startHour = 8)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = dayOffset,
            StartTime = new LocalTime(startHour, 0),
            Duration = Duration.FromHours(durationHours),
            MinVolunteers = min,
            MaxVolunteers = max,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        ShiftsDb.Shifts.Add(shift);
        return shift;
    }

    private void SeedSignup(Shift shift, Guid userId, SignupStatus status, Guid? blockId = null)
    {
        ShiftsDb.ShiftSignups.Add(new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = userId,
            Status = status,
            SignupBlockId = blockId,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        });
    }
}
