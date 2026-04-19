using AwesomeAssertions;
using Humans.Application.Enums;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class ShiftDashboardMetricsTests : IDisposable
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 7, 1, 12, 0);

    private readonly HumansDbContext _dbContext;
    private readonly ShiftManagementService _service;
    private readonly FakeClock _clock = new(TestNow);

    public ShiftDashboardMetricsTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ITeamService)).Returns(Substitute.For<ITeamService>());

        _service = new ShiftManagementService(
            _dbContext,
            Substitute.For<IAuditLogService>(),
            Substitute.For<IRoleAssignmentService>(),
            serviceProvider,
            new MemoryCache(new MemoryCacheOptions()),
            _clock,
            NullLogger<ShiftManagementService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetDashboardOverview_EmptyDatabase_ReturnsZeroCounters()
    {
        var es = await SeedEventAsync();

        var result = await _service.GetDashboardOverviewAsync(es.Id);

        result.TotalShifts.Should().Be(0);
        result.FilledShifts.Should().Be(0);
        result.TicketHolderCount.Should().Be(0);
        result.TicketHoldersEngaged.Should().Be(0);
        result.NonTicketSignups.Should().Be(0);
        result.StalePendingCount.Should().Be(0);
        result.Departments.Should().BeEmpty();
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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
        _dbContext.ShiftSignups.Add(signup);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDashboardOverviewAsync(es.Id);

        result.StalePendingCount.Should().Be(0);
    }

    [Fact]
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
        _dbContext.ShiftSignups.Add(signup);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDashboardOverviewAsync(es.Id);

        result.StalePendingCount.Should().Be(1);
    }

    [Fact]
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

    [Fact]
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
        infraRow.Subgroups.Should().HaveCount(3);

        // "Direct" pinned first regardless of fill %.
        infraRow.Subgroups[0].IsDirect.Should().BeTrue();
        infraRow.Subgroups[0].Name.Should().Be("Direct");

        // Remaining: Plumbing before Power (lower fill %).
        infraRow.Subgroups[1].Name.Should().Be("Plumbing");
        infraRow.Subgroups[2].Name.Should().Be("Power");

        // Invariant: subgroup totals sum to department totals.
        infraRow.Subgroups.Sum(s => s.TotalShifts).Should().Be(infraRow.TotalShifts);
        infraRow.Subgroups.Sum(s => s.FilledShifts).Should().Be(infraRow.FilledShifts);
        infraRow.Subgroups.Sum(s => s.SlotsRemaining).Should().Be(infraRow.SlotsRemaining);
        infraRow.Subgroups.Sum(s => s.Event.Total).Should().Be(infraRow.Event.Total);
        infraRow.Subgroups.Sum(s => s.Event.Filled).Should().Be(infraRow.Event.Filled);
    }

    [Fact]
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

    [Fact]
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
        result[0].Coordinators[0].DisplayName.Should().Be("coord");
    }

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task GetDashboardTrends_CountsSignupsInToday()
    {
        var es = await SeedEventAsync();
        var team = await SeedTeamAsync("T");
        var rota = await SeedRotaAsync(team, es, RotaPeriod.Event);
        var shift = await SeedShiftAsync(rota, dayOffset: 1, min: 1, max: 3);
        var user = await SeedUserAsync("u");

        _dbContext.ShiftSignups.Add(new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = user.Id,
            Status = SignupStatus.Pending,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetDashboardTrendsAsync(es.Id, TrendWindow.Last7Days);

        result[^1].NewSignups.Should().Be(1);
        result.Take(6).Should().OnlyContain(p => p.NewSignups == 0);
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
        _dbContext.EventSettings.Add(es);
        await _dbContext.SaveChangesAsync();
        return es;
    }

    private async Task<Team> SeedTeamAsync(string name, Guid? parentId = null)
    {
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = name.ToLowerInvariant(),
            IsActive = true,
            ParentTeamId = parentId,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        };
        _dbContext.Teams.Add(team);
        await _dbContext.SaveChangesAsync();
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
        _dbContext.Rotas.Add(rota);
        await _dbContext.SaveChangesAsync();
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
        _dbContext.Shifts.Add(shift);
        await _dbContext.SaveChangesAsync();
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
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        return user;
    }

    private async Task SeedSignupsAsync(Shift shift, SignupStatus status, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var user = await SeedUserAsync($"u-{shift.Id:N}-{i}");
            _dbContext.ShiftSignups.Add(new ShiftSignup
            {
                Id = Guid.NewGuid(),
                ShiftId = shift.Id,
                UserId = user.Id,
                Status = status,
                CreatedAt = TestNow.Minus(Duration.FromHours(1)),
                UpdatedAt = TestNow,
            });
        }
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedOneSignupAsync(Shift shift, Guid userId, SignupStatus status)
    {
        _dbContext.ShiftSignups.Add(new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = userId,
            Status = status,
            CreatedAt = TestNow.Minus(Duration.FromHours(1)),
            UpdatedAt = TestNow,
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedTicketOrderAsync(Guid userId, TicketPaymentStatus status)
    {
        _dbContext.TicketOrders.Add(new TicketOrder
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
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedCoordinatorAsync(Team team, User user)
    {
        _dbContext.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = user.Id,
            Role = TeamMemberRole.Coordinator,
            JoinedAt = TestNow.Minus(Duration.FromDays(30)),
        });
        await _dbContext.SaveChangesAsync();
    }
}
