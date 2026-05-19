using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Enums;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Shifts;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Shifts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Shifts;

public sealed class ShiftDashboardMetricsTests : ServiceTestHarness
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 7, 1, 12, 0);

    private readonly ShiftManagementService _service;

    public ShiftDashboardMetricsTests()
        : base(TestNow)
    {
        // The dashboard compute methods now reach cross-domain data through
        // ITicketQueryService / ITeamService / IUserService. Wire thin fakes that
        // read from the same in-memory DbContext so existing DbContext-based
        // test seed helpers still drive the scenarios end-to-end. The repository
        // is backed by the same in-memory options via TestDbContextFactory.
        var serviceProvider = new ServiceLocatorBuilder()
            .With<ITeamService>(new FakeTeamService(Db))
            .With<ITicketQueryService>(new FakeTicketQueryService(Db))
            .With<IUserService>(new FakeUserService(Db))
            .With<IRoleAssignmentService>()
            .Build();

        var repo = new ShiftManagementRepository(DbFactory);

        _service = new ShiftManagementService(
            repo,
            AuditLog,
            AdminAuthorization,
            serviceProvider,
            Cache,
            Substitute.For<IShiftViewInvalidator>(),
            Clock,
            NullLogger<ShiftManagementService>.Instance);
    }

    [HumansFact]
    public async Task GetDashboardOverview_EmptyDatabase_ReturnsZeroCounters()
    {
        var es = await SeedEventAsync();

        var result = await _service.GetDashboardOverviewAsync(es.Id);

        result.TotalShifts.Should().Be(0);
        result.FilledShifts.Should().Be(0);
        result.TotalSlots.Should().Be(0);
        result.FilledSlots.Should().Be(0);
        result.TicketHolderCount.Should().Be(0);
        result.TicketHoldersEngaged.Should().Be(0);
        result.NonTicketSignups.Should().Be(0);
        result.StalePendingCount.Should().Be(0);
        result.Departments.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetDashboardOverview_SumsSlotTotalsAcrossShifts()
    {
        // Slot-level counters are independent from "shift at min" counters:
        // a shift can be below min yet still contribute filled slots toward TotalSlots.
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Event);

        var s1 = await SeedShiftAsync(rota, dayOffset: 1, min: 3, max: 5);
        await SeedSignupsAsync(s1, SignupStatus.Confirmed, count: 2); // below min — 0 shifts at min, 2 slots filled
        var s2 = await SeedShiftAsync(rota, dayOffset: 2, min: 1, max: 4);
        await SeedSignupsAsync(s2, SignupStatus.Confirmed, count: 4); // at max — 1 shift at min, 4 slots filled

        var result = await _service.GetDashboardOverviewAsync(es.Id);

        result.TotalShifts.Should().Be(2);
        result.FilledShifts.Should().Be(1);
        result.TotalSlots.Should().Be(9);
        result.FilledSlots.Should().Be(6);
    }

    [HumansFact]
    public async Task GetDashboardOverview_FilledSlotsCapsAtMaxVolunteers()
    {
        // Overfilling a shift (confirmed > max) must not inflate FilledSlots beyond MaxVolunteers.
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Event);
        var shift = await SeedShiftAsync(rota, dayOffset: 1, min: 1, max: 3);
        await SeedSignupsAsync(shift, SignupStatus.Confirmed, count: 5);

        var result = await _service.GetDashboardOverviewAsync(es.Id);

        result.TotalSlots.Should().Be(3);
        result.FilledSlots.Should().Be(3);
    }

    [HumansFact]
    public async Task GetDashboardOverview_MinVolunteersMinusOne_NotFilled()
    {
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Event);
        var shift = await SeedShiftAsync(rota, dayOffset: 2, min: 3, max: 5);
        await SeedSignupsAsync(shift, SignupStatus.Confirmed, count: 2);

        var result = await _service.GetDashboardOverviewAsync(es.Id);

        result.TotalShifts.Should().Be(1);
        result.FilledShifts.Should().Be(0);
    }

    [HumansFact]
    public async Task GetDashboardOverview_MinVolunteersExactly_Filled()
    {
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Event);
        var shift = await SeedShiftAsync(rota, dayOffset: 2, min: 3, max: 5);
        await SeedSignupsAsync(shift, SignupStatus.Confirmed, count: 3);

        var result = await _service.GetDashboardOverviewAsync(es.Id);

        result.FilledShifts.Should().Be(1);
    }

    [HumansFact]
    public async Task GetDashboardOverview_PendingSignupsDoNotCountAsFilled()
    {
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Event);
        var shift = await SeedShiftAsync(rota, dayOffset: 2, min: 2, max: 5);
        await SeedSignupsAsync(shift, SignupStatus.Pending, count: 5);

        var result = await _service.GetDashboardOverviewAsync(es.Id);

        result.FilledShifts.Should().Be(0);
    }

    [HumansFact]
    public async Task GetDashboardOverview_AdminOnlyAndHiddenRotaShiftsExcluded()
    {
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var visibleRota = await SeedRotaAsync(team, es, RotaPeriod.Event);
        var hiddenRota = await SeedRotaAsync(team, es, RotaPeriod.Event, isVisible: false);
        await SeedShiftAsync(visibleRota, dayOffset: 2, min: 2, max: 5);
        await SeedShiftAsync(visibleRota, dayOffset: 3, min: 2, max: 5, adminOnly: true);
        await SeedShiftAsync(hiddenRota, dayOffset: 4, min: 2, max: 5);

        var result = await _service.GetDashboardOverviewAsync(es.Id);

        result.TotalShifts.Should().Be(1);
    }

    [HumansFact]
    public async Task GetDashboardOverview_TicketHolderAndEngagementCounters()
    {
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Event);
        var shift = await SeedShiftAsync(rota, dayOffset: 2, min: 1, max: 5);

        var engagedTicketHolder = await SeedUserAsync("A");
        var disengagedTicketHolder = await SeedUserAsync("B");
        var refundedUser = await SeedUserAsync("C");
        var nonTicketSignupUser = await SeedUserAsync("D");
        var cancelledOnlyTicketHolder = await SeedUserAsync("E");

        await SeedTicketOrderAsync(engagedTicketHolder.Id, TicketPaymentStatus.Paid);
        await SeedTicketOrderAsync(engagedTicketHolder.Id, TicketPaymentStatus.Paid); // duplicate — counts once
        await SeedTicketOrderAsync(disengagedTicketHolder.Id, TicketPaymentStatus.Paid);
        await SeedTicketOrderAsync(refundedUser.Id, TicketPaymentStatus.Refunded);
        await SeedTicketOrderAsync(cancelledOnlyTicketHolder.Id, TicketPaymentStatus.Paid);

        await SeedOneSignupAsync(shift, engagedTicketHolder.Id, SignupStatus.Confirmed);
        await SeedOneSignupAsync(shift, nonTicketSignupUser.Id, SignupStatus.Pending);
        await SeedOneSignupAsync(shift, cancelledOnlyTicketHolder.Id, SignupStatus.Cancelled);

        var result = await _service.GetDashboardOverviewAsync(es.Id);

        result.TicketHolderCount.Should().Be(3); // engaged, disengaged, cancelled-only
        result.TicketHoldersEngaged.Should().Be(1); // only A
        result.NonTicketSignups.Should().Be(1); // only D
    }

    [HumansFact(Timeout = 10000)]
    public async Task GetDashboardOverview_StalePending_ThresholdExact3Days_NotStale()
    {
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Event);
        var shift = await SeedShiftAsync(rota, dayOffset: 2, min: 1, max: 5);
        var user = await SeedUserAsync("U");

        var signup = new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = user.Id,
            Status = SignupStatus.Pending,
            CreatedAt = TestNow.Minus(Duration.FromDays(3)),
            UpdatedAt = TestNow,
        };
        Db.ShiftSignups.Add(signup);
        await Db.SaveChangesAsync();

        var result = await _service.GetDashboardOverviewAsync(es.Id);

        result.StalePendingCount.Should().Be(0);
    }

    [HumansFact]
    public async Task GetDashboardOverview_StalePending_JustPastThreshold_CountsAsStale()
    {
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Event);
        var shift = await SeedShiftAsync(rota, dayOffset: 2, min: 1, max: 5);
        var user = await SeedUserAsync("U");

        var signup = new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = user.Id,
            Status = SignupStatus.Pending,
            CreatedAt = TestNow.Minus(Duration.FromDays(3)).Minus(Duration.FromMinutes(1)),
            UpdatedAt = TestNow,
        };
        Db.ShiftSignups.Add(signup);
        await Db.SaveChangesAsync();

        var result = await _service.GetDashboardOverviewAsync(es.Id);

        result.StalePendingCount.Should().Be(1);
    }

    [HumansFact]
    public async Task GetDashboardOverview_NoSubteamRotas_DepartmentSubgroupsEmpty()
    {
        var es = await SeedEventAsync();
        var gate = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(gate, es, RotaPeriod.Event);
        await SeedShiftAsync(rota, dayOffset: 2, min: 1, max: 5);

        var result = await _service.GetDashboardOverviewAsync(es.Id);

        result.Departments.Should().HaveCount(1);
        result.Departments[0].Subgroups.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetDashboardOverview_WithSubteamRotas_ProducesSubgroupsWithDirectPinned()
    {
        var es = await SeedEventAsync();
        var infra = await SeedTeamAsync("Infrastructure");
        var power = await SeedTeamAsync("Power", parentId: infra.Id);
        var plumbing = await SeedTeamAsync("Plumbing", parentId: infra.Id);

        var infraDirect = await SeedRotaAsync(infra, es, RotaPeriod.Event);
        var powerRota = await SeedRotaAsync(power, es, RotaPeriod.Event);
        var plumbingRota = await SeedRotaAsync(plumbing, es, RotaPeriod.Event);

        // Infrastructure direct: 2 shifts, 1 filled.
        var d1 = await SeedShiftAsync(infraDirect, dayOffset: 1, min: 1, max: 3);
        var d2 = await SeedShiftAsync(infraDirect, dayOffset: 2, min: 1, max: 3);
        await SeedSignupsAsync(d1, SignupStatus.Confirmed, count: 1);

        // Power: 2 shifts, both filled (high fill %).
        var p1 = await SeedShiftAsync(powerRota, dayOffset: 1, min: 1, max: 3);
        var p2 = await SeedShiftAsync(powerRota, dayOffset: 2, min: 1, max: 3);
        await SeedSignupsAsync(p1, SignupStatus.Confirmed, count: 2);
        await SeedSignupsAsync(p2, SignupStatus.Confirmed, count: 2);

        // Plumbing: 2 shifts, 0 filled (lowest fill %).
        await SeedShiftAsync(plumbingRota, dayOffset: 1, min: 1, max: 3);
        await SeedShiftAsync(plumbingRota, dayOffset: 2, min: 1, max: 3);

        var result = await _service.GetDashboardOverviewAsync(es.Id);

        var infraRow = result.Departments.Single(d => string.Equals(d.DepartmentName, "Infrastructure", StringComparison.Ordinal));
        infraRow.TotalShifts.Should().Be(6);
        infraRow.FilledShifts.Should().Be(3); // 1 direct + 2 power
        infraRow.TotalSlots.Should().Be(18); // 6 shifts × max=3
        infraRow.FilledSlots.Should().Be(5); // 1 direct + 2 power shifts × 2 confirmed
        infraRow.Subgroups.Should().HaveCount(3);

        // "Direct" pinned first regardless of fill %.
        infraRow.Subgroups[0].IsDirect.Should().BeTrue();
        infraRow.Subgroups[0].Name.Should().Be("Direct");

        // Remaining subgroups sorted by lowest slot fill % first:
        // Plumbing (0/6 = 0%) before Power (4/6 ≈ 67%).
        infraRow.Subgroups[1].Name.Should().Be("Plumbing");
        infraRow.Subgroups[2].Name.Should().Be("Power");

        // Invariant: subgroup totals sum to department totals.
        infraRow.Subgroups.Sum(s => s.TotalShifts).Should().Be(infraRow.TotalShifts);
        infraRow.Subgroups.Sum(s => s.FilledShifts).Should().Be(infraRow.FilledShifts);
        infraRow.Subgroups.Sum(s => s.TotalSlots).Should().Be(infraRow.TotalSlots);
        infraRow.Subgroups.Sum(s => s.FilledSlots).Should().Be(infraRow.FilledSlots);
        infraRow.Subgroups.Sum(s => s.SlotsRemaining).Should().Be(infraRow.SlotsRemaining);
        infraRow.Subgroups.Sum(s => s.Event.Total).Should().Be(infraRow.Event.Total);
        infraRow.Subgroups.Sum(s => s.Event.Filled).Should().Be(infraRow.Event.Filled);
        infraRow.Subgroups.Sum(s => s.Event.TotalSlots).Should().Be(infraRow.Event.TotalSlots);
        infraRow.Subgroups.Sum(s => s.Event.FilledSlots).Should().Be(infraRow.Event.FilledSlots);
    }

    [HumansFact]
    public async Task GetDashboardOverview_DepartmentsSortedByLowestFillPercentFirst()
    {
        var es = await SeedEventAsync();
        var gate = await SeedTeamAsync("Gate");
        var kitchen = await SeedTeamAsync("Kitchen");

        // Gate: fully filled.
        var gateRota = await SeedRotaAsync(gate, es, RotaPeriod.Event);
        var gs = await SeedShiftAsync(gateRota, dayOffset: 1, min: 1, max: 3);
        await SeedSignupsAsync(gs, SignupStatus.Confirmed, count: 1);

        // Kitchen: not filled.
        var kitchenRota = await SeedRotaAsync(kitchen, es, RotaPeriod.Event);
        await SeedShiftAsync(kitchenRota, dayOffset: 1, min: 1, max: 3);

        var result = await _service.GetDashboardOverviewAsync(es.Id);

        result.Departments.Should().HaveCount(2);
        result.Departments[0].DepartmentName.Should().Be("Kitchen"); // 0% fill comes first
        result.Departments[1].DepartmentName.Should().Be("Gate");
    }

    [HumansFact]
    public async Task GetCoordinatorActivity_TeamsWithoutPendingExcluded()
    {
        var es = await SeedEventAsync();
        var gate = await SeedTeamAsync("Gate");
        var kitchen = await SeedTeamAsync("Kitchen");

        var gateRota = await SeedRotaAsync(gate, es, RotaPeriod.Event);
        var gs = await SeedShiftAsync(gateRota, dayOffset: 1, min: 1, max: 3);

        var coord = await SeedUserAsync("coord", TestNow.Minus(Duration.FromDays(2)));
        await SeedCoordinatorAsync(gate, coord);

        // Pending on Gate, not Kitchen.
        await SeedSignupsAsync(gs, SignupStatus.Pending, count: 1);

        var kitchenRota = await SeedRotaAsync(kitchen, es, RotaPeriod.Event);
        var ks = await SeedShiftAsync(kitchenRota, dayOffset: 1, min: 1, max: 3);
        await SeedSignupsAsync(ks, SignupStatus.Confirmed, count: 1);

        var result = await _service.GetCoordinatorActivityAsync(es.Id);

        result.Should().HaveCount(1);
        result[0].TeamName.Should().Be("Gate");
        result[0].PendingSignupCount.Should().Be(1);
        result[0].Coordinators.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task GetCoordinatorActivity_RangeSignup_CountsAsOneBlockNotOneRowPerDay()
    {
        var es = await SeedEventAsync();
        var build = await SeedTeamAsync("Build");

        var buildRota = await SeedRotaAsync(build, es, RotaPeriod.Build);

        // A 5-day range signup — five pending shift-signups sharing one SignupBlockId.
        var volunteer = await SeedUserAsync("v", TestNow);
        var blockId = Guid.NewGuid();
        for (var day = -5; day <= -1; day++)
        {
            var shift = await SeedShiftAsync(buildRota, dayOffset: day, min: 1, max: 3);
            Db.ShiftSignups.Add(new ShiftSignup
            {
                Id = Guid.NewGuid(),
                ShiftId = shift.Id,
                UserId = volunteer.Id,
                SignupBlockId = blockId,
                Status = SignupStatus.Pending,
                CreatedAt = TestNow.Minus(Duration.FromHours(1)),
                UpdatedAt = TestNow,
            });
        }
        await Db.SaveChangesAsync();

        var coord = await SeedUserAsync("coord", TestNow.Minus(Duration.FromDays(2)));
        await SeedCoordinatorAsync(build, coord);

        var result = await _service.GetCoordinatorActivityAsync(es.Id);

        result.Should().HaveCount(1);
        result[0].TeamName.Should().Be("Build");
        result[0].PendingSignupCount.Should().Be(1);
    }

    [HumansFact]
    public async Task GetCoordinatorActivity_SortsByOldestLoginFirst()
    {
        var es = await SeedEventAsync();
        var teamA = await SeedTeamAsync("A");
        var teamB = await SeedTeamAsync("B");

        var rotaA = await SeedRotaAsync(teamA, es, RotaPeriod.Event);
        var shiftA = await SeedShiftAsync(rotaA, dayOffset: 1, min: 1, max: 3);
        await SeedSignupsAsync(shiftA, SignupStatus.Pending, count: 1);

        var rotaB = await SeedRotaAsync(teamB, es, RotaPeriod.Event);
        var shiftB = await SeedShiftAsync(rotaB, dayOffset: 1, min: 1, max: 3);
        await SeedSignupsAsync(shiftB, SignupStatus.Pending, count: 1);

        // A has a recent login; B has an old login → B should come first.
        var coordA = await SeedUserAsync("a-coord", TestNow.Minus(Duration.FromHours(1)));
        var coordB = await SeedUserAsync("b-coord", TestNow.Minus(Duration.FromDays(20)));
        await SeedCoordinatorAsync(teamA, coordA);
        await SeedCoordinatorAsync(teamB, coordB);

        var result = await _service.GetCoordinatorActivityAsync(es.Id);

        result.Should().HaveCount(2);
        result[0].TeamName.Should().Be("B");
        result[1].TeamName.Should().Be("A");
    }

    [HumansFact]
    public async Task GetDashboardTrends_SevenDayWindow_ReturnsSevenPointsEndingToday()
    {
        var es = await SeedEventAsync();

        var result = await _service.GetDashboardTrendsAsync(es.Id, TrendWindow.Last7Days);

        result.Should().HaveCount(7);
        var today = TestNow.InUtc().Date;
        result[^1].Date.Should().Be(today);
        result[0].Date.Should().Be(today.PlusDays(-6));
        result.Should().OnlyContain(p => p.NewSignups == 0 && p.NewTicketSales == 0 && p.DistinctLogins == 0);
    }

    [HumansFact]
    public async Task GetDashboardTrends_CountsSignupsInToday()
    {
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("T");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Event);
        var shift = await SeedShiftAsync(rota, dayOffset: 1, min: 1, max: 3);
        var user = await SeedUserAsync("u");

        Db.ShiftSignups.Add(new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = user.Id,
            Status = SignupStatus.Pending,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        });
        await Db.SaveChangesAsync();

        var result = await _service.GetDashboardTrendsAsync(es.Id, TrendWindow.Last7Days);

        result[^1].NewSignups.Should().Be(1);
        result.Take(6).Should().OnlyContain(p => p.NewSignups == 0);
    }

    // ================================================================
    // GetDailyDepartmentStaffingAsync
    // ================================================================

    [HumansFact]
    public async Task GetDailyDepartmentStaffing_EventPeriod_ReturnsEmpty()
    {
        // Event day-over-day mix is deliberately omitted by design — the planning
        // flow for event shifts is different. Only Build and Strike produce data.
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Event);
        var shift = await SeedShiftAsync(rota, dayOffset: 2, min: 1, max: 3);
        await SeedSignupsAsync(shift, SignupStatus.Confirmed, count: 2);

        var result = await _service.GetDailyDepartmentStaffingAsync(es.Id, ShiftPeriod.Event);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetDailyDepartmentStaffing_NullPeriod_ReturnsEmpty()
    {
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Build);
        var shift = await SeedShiftAsync(rota, dayOffset: -3, min: 1, max: 3);
        await SeedSignupsAsync(shift, SignupStatus.Confirmed, count: 1);

        var result = await _service.GetDailyDepartmentStaffingAsync(es.Id, period: null);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetDailyDepartmentStaffing_BuildPeriod_CountsOnlyConfirmed()
    {
        // Pending signups must not inflate on-site counts — the chart is about
        // who is actually confirmed to show up that day.
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Build");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Build);
        var shift = await SeedShiftAsync(rota, dayOffset: -3, min: 1, max: 5);
        await SeedSignupsAsync(shift, SignupStatus.Confirmed, count: 2);
        await SeedSignupsAsync(shift, SignupStatus.Pending, count: 3);

        var result = await _service.GetDailyDepartmentStaffingAsync(es.Id, ShiftPeriod.Build);

        var dayRow = result.SingleOrDefault(r => r.Date == es.GateOpeningDate.PlusDays(-3));
        dayRow.Should().NotBeNull();
        dayRow.Departments.Should().ContainSingle(d => string.Equals(d.DepartmentName, "Build", StringComparison.Ordinal))
            .Which.ConfirmedCount.Should().Be(2);
    }

    [HumansFact]
    public async Task GetDailyDepartmentStaffing_NonPromotedSubteam_RollsUpToParent()
    {
        // Subteam shifts that AREN'T promoted to directory must roll up into the
        // parent department's stacked-bar segment, not appear as a separate one.
        var es = await SeedEventAsync();
        var infra = await SeedTeamAsync("Infrastructure");
        var power = await SeedTeamAsync("Power", parentId: infra.Id); // not promoted
        var powerRota = await SeedRotaAsync(power, es, RotaPeriod.Build);
        var shift = await SeedShiftAsync(powerRota, dayOffset: -2, min: 1, max: 5);
        await SeedSignupsAsync(shift, SignupStatus.Confirmed, count: 3);

        var result = await _service.GetDailyDepartmentStaffingAsync(es.Id, ShiftPeriod.Build);

        var dayRow = result.Single(r => r.Date == es.GateOpeningDate.PlusDays(-2));
        dayRow.Departments.Should().ContainSingle();
        dayRow.Departments[0].DepartmentName.Should().Be("Infrastructure");
        dayRow.Departments[0].ConfirmedCount.Should().Be(3);
    }

    // ================================================================
    // GetShiftDurationBreakdownAsync
    // ================================================================

    [HumansFact]
    public async Task GetShiftDurationBreakdown_NullPeriod_ReturnsEmpty()
    {
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Event);
        await SeedShiftAsync(rota, dayOffset: 1, min: 1, max: 3);

        var result = await _service.GetShiftDurationBreakdownAsync(es.Id, period: null);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetShiftDurationBreakdown_AllDayShifts_CollapseIntoSingleBucket()
    {
        // Full-day shifts share one bucket regardless of nominal duration
        // (6 h, 8 h, 10 h all render as "Full day" on the breakdown chart).
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Event);
        await SeedAllDayShiftAsync(rota, dayOffset: 1, nominalHours: 6, min: 1, max: 3);
        await SeedAllDayShiftAsync(rota, dayOffset: 2, nominalHours: 8, min: 1, max: 4);

        var result = await _service.GetShiftDurationBreakdownAsync(es.Id, ShiftPeriod.Event);

        result.Should().ContainSingle(r => r.IsAllDay);
        result.Single(r => r.IsAllDay).TotalSlots.Should().Be(7);
    }

    [HumansFact]
    public async Task GetShiftDurationBreakdown_DistinctHourlyDurations_GetSeparateBuckets()
    {
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Event);
        await SeedHourlyShiftAsync(rota, dayOffset: 1, hours: 2, min: 1, max: 3);
        await SeedHourlyShiftAsync(rota, dayOffset: 2, hours: 4, min: 1, max: 5);
        await SeedHourlyShiftAsync(rota, dayOffset: 3, hours: 4, min: 1, max: 5);

        var result = await _service.GetShiftDurationBreakdownAsync(es.Id, ShiftPeriod.Event);

        result.Should().HaveCount(2);
        result.Should().Contain(r => !r.IsAllDay && r.DurationHours == 2 && r.TotalSlots == 3);
        result.Should().Contain(r => !r.IsAllDay && r.DurationHours == 4 && r.TotalSlots == 10);
    }

    // ================================================================
    // GetCoverageHeatmapAsync
    // ================================================================

    [HumansFact]
    public async Task GetCoverageHeatmap_PromotedSubteam_GetsOwnRow()
    {
        // A subteam promoted to the directory gets its own heatmap row.
        // Its shifts must NOT also appear under the parent team's row
        // (the "no-overlap" invariant).
        var es = await SeedEventAsync();
        var infra = await SeedTeamAsync("Infrastructure");
        var power = await SeedTeamAsync("Power", parentId: infra.Id, isPromotedToDirectory: true);

        var infraRota = await SeedRotaAsync(infra, es, RotaPeriod.Event);
        var powerRota = await SeedRotaAsync(power, es, RotaPeriod.Event);
        await SeedShiftAsync(infraRota, dayOffset: 1, min: 1, max: 3);
        await SeedShiftAsync(powerRota, dayOffset: 1, min: 1, max: 4);

        var result = await _service.GetCoverageHeatmapAsync(es.Id, ShiftPeriod.Event);

        result.Rotas.Should().HaveCount(2);
        result.Rotas.Should().ContainSingle(r => string.Equals(r.RotaName, "Infrastructure", StringComparison.Ordinal))
            .Which.Cells.Sum(c => c.TotalSlots).Should().Be(3);
        result.Rotas.Should().ContainSingle(r => string.Equals(r.RotaName, "Power", StringComparison.Ordinal))
            .Which.Cells.Sum(c => c.TotalSlots).Should().Be(4);
    }

    [HumansFact]
    public async Task GetCoverageHeatmap_NonPromotedSubteam_RollsUpToParent()
    {
        // Non-promoted subteam: shifts appear only in the parent's row, never
        // as a separate row (no-overlap in the other direction).
        var es = await SeedEventAsync();
        var infra = await SeedTeamAsync("Infrastructure");
        var plumbing = await SeedTeamAsync("Plumbing", parentId: infra.Id); // not promoted
        var infraRota = await SeedRotaAsync(infra, es, RotaPeriod.Event);
        var plumbingRota = await SeedRotaAsync(plumbing, es, RotaPeriod.Event);

        await SeedShiftAsync(infraRota, dayOffset: 1, min: 1, max: 2);
        await SeedShiftAsync(plumbingRota, dayOffset: 1, min: 1, max: 3);

        var result = await _service.GetCoverageHeatmapAsync(es.Id, ShiftPeriod.Event);

        result.Rotas.Should().ContainSingle();
        result.Rotas[0].RotaName.Should().Be("Infrastructure");
        result.Rotas[0].Cells.Sum(c => c.TotalSlots).Should().Be(5);
        result.Rotas.Should().NotContain(r => string.Equals(r.RotaName, "Plumbing", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task GetCoverageHeatmap_FilledSlotsCapAtMaxVolunteers()
    {
        // Overfilled shifts must not inflate heatmap cell counts — same cap rule
        // as the overview card, applied per cell.
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Event);
        var shift = await SeedShiftAsync(rota, dayOffset: 1, min: 1, max: 3);
        await SeedSignupsAsync(shift, SignupStatus.Confirmed, count: 5);

        var result = await _service.GetCoverageHeatmapAsync(es.Id, ShiftPeriod.Event);

        var cell = result.Rotas.Single().Cells.Single(c => c.TotalSlots > 0);
        cell.TotalSlots.Should().Be(3);
        cell.FilledSlots.Should().Be(3);
    }

    [HumansFact]
    public async Task GetCoverageHeatmap_DayPeriodClassification_UsesShiftPeriodEnum()
    {
        // Day-column period tagging must use the ShiftPeriod enum, not English
        // strings — previously the service emitted "Set-up"/"Event"/"Strike"
        // which coupled the view to magic values.
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Event);
        await SeedShiftAsync(rota, dayOffset: 1, min: 1, max: 2);

        var result = await _service.GetCoverageHeatmapAsync(es.Id, period: null);

        result.Days.Should().Contain(d => d.DayOffset < 0 && d.Period == ShiftPeriod.Build);
        result.Days.Should().Contain(d => d.DayOffset >= 0 && d.DayOffset <= es.EventEndOffset && d.Period == ShiftPeriod.Event);
        result.Days.Should().Contain(d => d.DayOffset > es.EventEndOffset && d.Period == ShiftPeriod.Strike);
    }

    // ================================================================
    // BuildSubPeriod narrowing — Shifts.md invariant line 238
    // ================================================================

    [HumansFact]
    public async Task GetDashboardOverview_SubPeriodWithNonBuildPeriod_IsIgnored()
    {
        // Sub-period only narrows when period == Build. For any other period
        // the call must behave as if sub-period were null. Arrange a mixed
        // event with shifts spread across Build and Event periods.
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("Gate");
        var buildRota = await SeedRotaAsync(team, es, RotaPeriod.Build);
        var eventRota = await SeedRotaAsync(team, es, RotaPeriod.Event);

        // Build-period shift far in the past
        await SeedShiftAsync(buildRota, dayOffset: -10, min: 2, max: 4);
        // Event-period shifts
        await SeedShiftAsync(eventRota, dayOffset: 1, min: 2, max: 4);
        await SeedShiftAsync(eventRota, dayOffset: 3, min: 2, max: 4);

        // Act: period=Event with sub-period set (FirstCrew) and without.
        var withSubPeriod = await _service.GetDashboardOverviewAsync(
            es.Id, period: ShiftPeriod.Event, subPeriod: BuildSubPeriod.FirstCrew);
        var withoutSubPeriod = await _service.GetDashboardOverviewAsync(
            es.Id, period: ShiftPeriod.Event, subPeriod: null);

        // Assert: sub-period silently ignored when period != Build. The two
        // counters must agree on totals — anything else means narrowing
        // leaked into a period it should not.
        withSubPeriod.TotalShifts.Should().Be(withoutSubPeriod.TotalShifts);
        withSubPeriod.TotalSlots.Should().Be(withoutSubPeriod.TotalSlots);
        withSubPeriod.FilledShifts.Should().Be(withoutSubPeriod.FilledShifts);
    }

    // ================================================================
    // Seeding helpers.
    // ================================================================

    private async Task<EventSettings> SeedEventAsync()
    {
        var es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = "Test Event",
            Year = 2026,
            TimeZoneId = "UTC",
            GateOpeningDate = new LocalDate(2026, 8, 1),
            BuildStartOffset = -14,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsActive = true,
            CreatedAt = TestNow.Minus(Duration.FromDays(60)),
            UpdatedAt = TestNow,
        };
        Db.EventSettings.Add(es);
        await Db.SaveChangesAsync();
        return es;
    }

    private async Task<Team> SeedTeamAsync(string name, Guid? parentId = null, bool isPromotedToDirectory = false)
    {
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = name.ToLowerInvariant(),
            IsActive = true,
            ParentTeamId = parentId,
            IsPromotedToDirectory = isPromotedToDirectory,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        };
        Db.Teams.Add(team);
        await Db.SaveChangesAsync();
        return team;
    }

    private async Task<Rota> SeedRotaAsync(Team team, EventSettings es, RotaPeriod period, bool isVisible = true)
    {
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            EventSettingsId = es.Id,
            Name = $"{team.Name} {period}",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = period,
            IsVisibleToVolunteers = isVisible,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        };
        Db.Rotas.Add(rota);
        await Db.SaveChangesAsync();
        return rota;
    }

    private async Task<Shift> SeedShiftAsync(Rota rota, int dayOffset, int min, int max, bool adminOnly = false)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = dayOffset,
            StartTime = new LocalTime(9, 0),
            Duration = Duration.FromHours(4),
            MinVolunteers = min,
            MaxVolunteers = max,
            AdminOnly = adminOnly,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        };
        Db.Shifts.Add(shift);
        await Db.SaveChangesAsync();
        return shift;
    }

    private async Task<Shift> SeedAllDayShiftAsync(Rota rota, int dayOffset, double nominalHours, int min, int max)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = dayOffset,
            StartTime = new LocalTime(8, 0),
            Duration = Duration.FromHours(nominalHours),
            IsAllDay = true,
            MinVolunteers = min,
            MaxVolunteers = max,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        };
        Db.Shifts.Add(shift);
        await Db.SaveChangesAsync();
        return shift;
    }

    private async Task<Shift> SeedHourlyShiftAsync(Rota rota, int dayOffset, int hours, int min, int max)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = dayOffset,
            StartTime = new LocalTime(9, 0),
            Duration = Duration.FromHours(hours),
            IsAllDay = false,
            MinVolunteers = min,
            MaxVolunteers = max,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        };
        Db.Shifts.Add(shift);
        await Db.SaveChangesAsync();
        return shift;
    }

    private async Task<User> SeedUserAsync(string displayName, Instant? lastLogin = null)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = $"{displayName}@test",
            NormalizedUserName = $"{displayName.ToUpperInvariant()}@TEST",
            Email = $"{displayName}@test",
            NormalizedEmail = $"{displayName.ToUpperInvariant()}@TEST",
            DisplayName = displayName,
            LastLoginAt = lastLogin,
            SecurityStamp = Guid.NewGuid().ToString(),
            CreatedAt = TestNow,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync();
        return user;
    }

    private async Task SeedSignupsAsync(Shift shift, SignupStatus status, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var user = await SeedUserAsync($"u-{shift.Id:N}-{i}");
            Db.ShiftSignups.Add(new ShiftSignup
            {
                Id = Guid.NewGuid(),
                ShiftId = shift.Id,
                UserId = user.Id,
                Status = status,
                CreatedAt = TestNow.Minus(Duration.FromHours(1)),
                UpdatedAt = TestNow,
            });
        }
        await Db.SaveChangesAsync();
    }

    private async Task SeedOneSignupAsync(Shift shift, Guid userId, SignupStatus status)
    {
        Db.ShiftSignups.Add(new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = userId,
            Status = status,
            CreatedAt = TestNow.Minus(Duration.FromHours(1)),
            UpdatedAt = TestNow,
        });
        await Db.SaveChangesAsync();
    }

    private async Task SeedTicketOrderAsync(Guid userId, TicketPaymentStatus status)
    {
        Db.TicketOrders.Add(new TicketOrder
        {
            Id = Guid.NewGuid(),
            VendorOrderId = Guid.NewGuid().ToString("N"),
            BuyerName = "Buyer",
            BuyerEmail = $"{userId:N}@buyer.test",
            MatchedUserId = userId,
            PaymentStatus = status,
            Currency = "EUR",
            VendorEventId = "v1",
            PurchasedAt = TestNow.Minus(Duration.FromDays(10)),
            SyncedAt = TestNow,
        });
        await Db.SaveChangesAsync();
    }

    private async Task SeedCoordinatorAsync(Team team, User user)
    {
        Db.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = user.Id,
            Role = TeamMemberRole.Coordinator,
            JoinedAt = TestNow.Minus(Duration.FromDays(30)),
        });
        await Db.SaveChangesAsync();
    }

    // ================================================================
    // Thin fakes for cross-domain service interfaces. Each method covers
    // only the service surface used by ShiftManagementService's dashboard
    // compute paths. All reads go through the same in-memory DbContext
    // so the test seed helpers (Db.*.Add) drive results end-to-end.
    // ================================================================

    private sealed class FakeTicketQueryService(HumansDbContext db) : ITicketQueryService
    {
        public async Task<IReadOnlyCollection<Guid>> GetMatchedUserIdsForPaidOrdersAsync(CancellationToken ct = default)
        {
            return await db.TicketOrders
                .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid && o.MatchedUserId != null)
                .Select(o => o.MatchedUserId!.Value)
                .Distinct()
                .ToListAsync(ct);
        }

        public async Task<IReadOnlyList<Instant>> GetPaidOrderDatesInWindowAsync(Instant fromInclusive, Instant toExclusive, CancellationToken ct = default)
        {
            return await db.TicketOrders
                .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid
                            && o.PurchasedAt >= fromInclusive
                            && o.PurchasedAt < toExclusive)
                .Select(o => o.PurchasedAt)
                .ToListAsync(ct);
        }

        // Members below are unused by the dashboard compute paths under test.
        public Task<int> GetUserTicketCountAsync(Guid userId) => throw new NotSupportedException();
        public Task<HashSet<Guid>> GetUserIdsWithTicketsAsync() => throw new NotSupportedException();
        public Task<HashSet<Guid>> GetAllMatchedUserIdsAsync() => throw new NotSupportedException();
        public Task<IReadOnlySet<Guid>> GetMatchedUserIdsForYearAsync(int year, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<int>> GetMatchedTicketYearsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TicketDashboardStats> GetDashboardStatsAsync() => throw new NotSupportedException();
        public Task<decimal> GetGrossTicketRevenueAsync() => throw new NotSupportedException();
        public Task<BreakEvenResult> CalculateBreakEvenAsync(int ticketsSold, decimal grossRevenue, string currency, bool canAccessFinance, int fallbackTarget) => throw new NotSupportedException();
        public Task<TicketSalesAggregates> GetSalesAggregatesAsync() => throw new NotSupportedException();
        public Task<List<string>> GetAvailableTicketTypesAsync() => throw new NotSupportedException();
        public Task<CodeTrackingData> GetCodeTrackingDataAsync(string? search) => throw new NotSupportedException();
        public Task<OrdersPageResult> GetOrdersPageAsync(string? search, string sortBy, bool sortDesc, int page, int pageSize, string? filterPaymentStatus, string? filterTicketType, bool? filterMatched) => throw new NotSupportedException();
        public Task<AttendeesPageResult> GetAttendeesPageAsync(string? search, string sortBy, bool sortDesc, int page, int pageSize, string? filterTicketType, string? filterStatus, bool? filterMatched, string? filterOrderId, bool filterMultipleTickets = false) => throw new NotSupportedException();
        public Task<WhoHasntBoughtResult> GetWhoHasntBoughtAsync(string? search, string? filterTeam, string? filterTier, string? filterTicketStatus, int page, int pageSize) => throw new NotSupportedException();
        public Task<List<AttendeeExportRow>> GetAttendeeExportDataAsync() => throw new NotSupportedException();
        public Task<List<OrderExportRow>> GetOrderExportDataAsync() => throw new NotSupportedException();
        public Task<bool> HasTicketAttendeeMatchAsync(Guid userId) => throw new NotSupportedException();
        public Task<List<UserTicketOrderSummary>> GetUserTicketOrderSummariesAsync(Guid userId) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetOpenTicketIdsForUserAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> HasCurrentEventTicketAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<UserTicketExportData> GetUserTicketExportDataAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Instant?> GetPostEventHoldDateAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void InvalidateAfterTransfer(Guid senderUserId, Guid? receiverUserId) => throw new NotSupportedException();
        public void InvalidateAfterContactImport() => throw new NotSupportedException();
        public Task<UserTicketHoldings> GetUserTicketHoldingsAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeUserService(HumansDbContext db) : IUserService
    {
        public ValueTask<UserInfo?> GetUserInfoAsync(Guid userId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyCollection<UserInfo>> GetAllUserInfosAsync(CancellationToken ct = default)
        {
            // Build a UserInfo snapshot from the in-memory DB so dashboard-metrics
            // tests that drive ComputeDashboardTrendsAsync (which now filters
            // LastLoginAt off the cached snapshot) can observe the fake's data.
            var users = db.Users.ToList();
            IReadOnlyCollection<UserInfo> result = users.Select(u => UserInfo.Create(
                u,
                userEmails: [],
                eventParticipations: [],
                externalLogins: [],
                profile: null,
                contactFields: [],
                profileLanguages: [],
                volunteerHistory: [],
                communicationPreferences: [])).ToList();
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<HumanSearchResult>> SearchUsersAsync(
            string query, Humans.Application.Services.Profiles.PersonSearchFields fields,
            int limit = 10, CancellationToken ct = default) => throw new NotSupportedException();

        public async Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default)
            => await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        public async Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
        {
            if (userIds.Count == 0) return new Dictionary<Guid, User>();
            var users = await db.Users.Include(u => u.UserEmails).Where(u => userIds.Contains(u.Id)).ToListAsync(ct);
            return users.ToDictionary(u => u.Id);
        }

        public async ValueTask<IReadOnlyDictionary<Guid, UserInfo>> GetUserInfosAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
        {
            var snapshot = (await GetAllUserInfosAsync(ct)).ToDictionary(u => u.Id);
            var dict = new Dictionary<Guid, UserInfo>();
            foreach (var id in userIds)
                if (snapshot.TryGetValue(id, out var info))
                    dict[id] = info;
            return dict;
        }

        public async Task<IReadOnlyList<Instant>> GetLoginTimestampsInWindowAsync(Instant fromInclusive, Instant toExclusive, CancellationToken ct = default)
        {
            return await db.Users
                .Where(u => u.LastLoginAt != null && u.LastLoginAt >= fromInclusive && u.LastLoginAt < toExclusive)
                .Select(u => u.LastLoginAt!.Value)
                .ToListAsync(ct);
        }

        // Members below are unused by the dashboard compute paths under test.
        public Task<EventParticipation?> GetParticipationAsync(Guid userId, int year, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<EventParticipation>> GetAllParticipationsForYearAsync(int year, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EventParticipation> DeclareNotAttendingAsync(Guid userId, int year, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetParticipationFromTicketSyncAsync(Guid userId, int year, ParticipationStatus status, Instant? checkedInAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OnsiteUserRow>> GetOnsiteUsersAsync(int year, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> BackfillParticipationsAsync(int year, List<(Guid UserId, ParticipationStatus Status)> entries, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<User>> GetAllUsersAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TrySetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TrySetGoogleEmailStatusFromSyncAsync(Guid userId, GoogleEmailStatus status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetPreferredLanguageAsync(Guid userId, string preferredLanguage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetICalTokenAsync(Guid userId, Guid token, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SetDeletionPendingAsync(Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<User?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<User>> GetContactUsersAsync(string? search, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Guid?> GetOtherUserIdHavingGoogleEmailAsync(string email, Guid excludeUserId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> PurgeOwnDataAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetLastConsentReminderSentAsync(Guid userId, Instant sentAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetRejectedGoogleEmailCountAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetCountByContactSourceAsync(ContactSource source, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(Instant now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> AnonymizeForMergeAsync(Guid sourceUserId, Guid targetUserId, Instant now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> ReassignLoginsToUserAsync(Guid sourceUserId, Guid targetUserId, Instant updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> ReassignEventParticipationToUserAsync(Guid sourceUserId, Guid targetUserId, Instant updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlySet<Guid>> GetMergedSourceIdsAsync(Guid targetUserId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetUsersWithLoginsButNoEmailsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeleteUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeleteAllExternalLoginsForUserAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>> GetExternalLoginsByUserIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeTeamService(HumansDbContext db) : ITeamService
    {
        public async Task<IReadOnlyDictionary<Guid, Team>> GetByIdsWithParentsAsync(IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken = default)
        {
            if (teamIds.Count == 0) return new Dictionary<Guid, Team>();
            var requested = await db.Teams.Where(t => teamIds.Contains(t.Id)).ToListAsync(cancellationToken);
            var parentIds = requested
                .Where(t => t.ParentTeamId.HasValue)
                .Select(t => t.ParentTeamId!.Value)
                .Where(id => !teamIds.Contains(id))
                .Distinct()
                .ToList();
            var parents = parentIds.Count == 0
                ? []
                : await db.Teams.Where(t => parentIds.Contains(t.Id)).ToListAsync(cancellationToken);
            var dict = new Dictionary<Guid, Team>();
            foreach (var t in requested) dict[t.Id] = t;
            foreach (var t in parents) dict[t.Id] = t;
            return dict;
        }

        public async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsAsync(CancellationToken cancellationToken = default)
        {
            var teams = await db.Teams
                .Include(t => t.Members)
                .ToListAsync(cancellationToken);
            return teams.ToDictionary(
                t => t.Id,
                t => new TeamInfo(
                    t.Id, t.Name, t.Description, t.Slug,
                    t.IsActive, t.IsSystemTeam, t.SystemTeamType, t.RequiresApproval,
                    t.IsPublicPage, t.IsHidden, t.IsPromotedToDirectory,
                    t.CreatedAt,
                    t.Members
                        .Where(m => m.LeftAt is null)
                        .Select(m => new TeamMemberInfo(
                            m.Id, m.UserId, string.Empty, null, null, m.Role, m.JoinedAt))
                        .ToList(),
                    t.ParentTeamId));
        }

        // Members below are unused by the dashboard compute paths under test.
        public Task<Team> CreateTeamAsync(string name, string? description, bool requiresApproval, Guid? parentTeamId = null, string? googleGroupPrefix = null, bool isHidden = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Team?> GetTeamEntityBySlugAsync(string slug, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamInfo?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Team?> GetTeamByIdAsync(Guid teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamInfo?> GetTeamAsync(Guid teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Team>> GetAllTeamsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamSearchHit>> SearchAsync(string query, int max, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamDirectoryResult> GetTeamDirectoryAsync(Guid? userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamDetailResult?> GetTeamDetailAsync(string slug, Guid? userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamMember>> GetUserTeamsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyTeamMembershipSummary>> GetMyTeamMembershipsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Team> UpdateTeamAsync(Guid teamId, string name, string? description, bool requiresApproval, bool isActive, Guid? parentTeamId = null, string? googleGroupPrefix = null, string? customSlug = null, bool? hasBudget = null, bool? isHidden = null, bool? isSensitive = null, bool? isPromotedToDirectory = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTeamAsync(Guid teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamJoinRequest> RequestToJoinTeamAsync(Guid teamId, Guid userId, string? message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamMember> JoinTeamDirectlyAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> LeaveTeamAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WithdrawJoinRequestAsync(Guid requestId, Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamMember> ApproveJoinRequestAsync(Guid requestId, Guid approverUserId, string? notes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RejectJoinRequestAsync(Guid requestId, Guid approverUserId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamJoinRequestSnapshot>> GetPendingRequestsForTeamAsync(Guid teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamJoinRequestSnapshot?> GetUserPendingRequestAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsUserCoordinatorOfTeamAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RemoveMemberAsync(Guid teamId, Guid userId, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, string>> GetManagementRoleNamesByTeamIdsAsync(IEnumerable<Guid> teamIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Updated, string? PreviousPrefix)> SetGoogleGroupPrefixAsync(Guid teamId, string? prefix, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdminTeamListResult> GetAdminTeamListAsync(int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamRosterSlotSummary>> GetRosterAsync(string? priority, string? status, string? period, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamMember> AddMemberToTeamAsync(Guid teamId, Guid targetUserId, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetMemberRoleAsync(Guid teamId, Guid userId, TeamMemberRole role, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamPageUpdateResult> UpdateTeamPageContentAsync(Guid teamId, string? pageContent, IReadOnlyList<TeamPageCallToActionInput> callsToAction, bool isPublicPage, bool showCoordinatorsOnPublicPage, Guid updatedByUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamRoleDefinition> CreateRoleDefinitionAsync(Guid teamId, string name, string? description, int slotCount, List<SlotPriority> priorities, int sortOrder, RolePeriod period, Guid actorUserId, bool isPublic = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamRoleDefinition> UpdateRoleDefinitionAsync(Guid roleDefinitionId, string name, string? description, int slotCount, List<SlotPriority> priorities, int sortOrder, bool isManagement, RolePeriod period, Guid actorUserId, bool isPublic = true, bool canToggleManagement = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteRoleDefinitionAsync(Guid roleDefinitionId, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamRoleManagementToggleResult> ToggleRoleIsManagementAsync(Guid roleDefinitionId, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamRoleDefinitionSnapshot>> GetRoleDefinitionsAsync(Guid teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamRoleAssignment> AssignToRoleAsync(Guid roleDefinitionId, Guid targetUserId, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UnassignFromRoleAsync(Guid roleDefinitionId, Guid teamMemberId, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetUserCoordinatedTeamIdsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamMember> AddSeededMemberAsync(Guid teamId, Guid userId, TeamMemberRole role, Instant joinedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> PermanentlyDeleteTeamAsync(Guid teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void RemoveMemberFromAllTeamsCache(Guid userId) => throw new NotSupportedException();
        public void InvalidateActiveTeamsCache() => throw new NotSupportedException();
        public Task<IReadOnlyList<Models.TeamMembership>> GetActiveTeamMembershipsForUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnqueueGoogleResyncForUserTeamsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RevokeAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReassignToUserAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetEffectiveBudgetCoordinatorTeamIdsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> GetTotalPendingJoinRequestCountAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamRoleReconciliationMembership>> GetActiveMembershipsForRoleReconciliationAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ApplyMemberRoleChangesAsync(IReadOnlyCollection<(Guid TeamMemberId, TeamMemberRole Role)> changes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ApplySystemTeamMembershipDeltaAsync(Guid teamId, IReadOnlyCollection<Guid> userIdsToAdd, IReadOnlyCollection<Guid> userIdsToRemove, Instant now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
