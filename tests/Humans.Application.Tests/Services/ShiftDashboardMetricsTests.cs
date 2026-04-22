using AwesomeAssertions;
using Humans.Application.Enums;
using Humans.Application.Interfaces;
using Humans.Application.Services.Shifts;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories;
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

        // The dashboard compute methods now reach cross-domain data through
        // ITicketQueryService / ITeamService / IUserService. Wire thin fakes that
        // read from the same in-memory DbContext so existing DbContext-based
        // test seed helpers still drive the scenarios end-to-end. The repository
        // is backed by the same in-memory options via TestDbContextFactory.
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ITeamService)).Returns(new FakeTeamService(_dbContext));
        serviceProvider.GetService(typeof(ITicketQueryService)).Returns(new FakeTicketQueryService(_dbContext));
        serviceProvider.GetService(typeof(IUserService)).Returns(new FakeUserService(_dbContext));

        var repo = new ShiftManagementRepository(new TestDbContextFactory(options));

        _service = new ShiftManagementService(
            repo,
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
            _dbContext.ShiftSignups.Add(new ShiftSignup
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
        await _dbContext.SaveChangesAsync();

        var coord = await SeedUserAsync("coord", TestNow.Minus(Duration.FromDays(2)));
        await SeedCoordinatorAsync(build, coord);

        var result = await _service.GetCoordinatorActivityAsync(es.Id);

        result.Should().HaveCount(1);
        result[0].TeamName.Should().Be("Build");
        result[0].PendingSignupCount.Should().Be(1);
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

    // ================================================================
    // Thin fakes for cross-domain service interfaces. Each method covers
    // only the narrow surface used by ShiftManagementService's dashboard
    // compute paths. All reads go through the same in-memory DbContext
    // so the test seed helpers (_dbContext.*.Add) drive results end-to-end.
    // ================================================================

    private sealed class FakeTicketQueryService : ITicketQueryService
    {
        private readonly HumansDbContext _db;
        public FakeTicketQueryService(HumansDbContext db) => _db = db;

        public async Task<IReadOnlyCollection<Guid>> GetMatchedUserIdsForPaidOrdersAsync(CancellationToken ct = default)
        {
            return await _db.TicketOrders
                .Where(o => o.PaymentStatus == Humans.Domain.Enums.TicketPaymentStatus.Paid && o.MatchedUserId != null)
                .Select(o => o.MatchedUserId!.Value)
                .Distinct()
                .ToListAsync(ct);
        }

        public async Task<IReadOnlyList<Instant>> GetPaidOrderDatesInWindowAsync(Instant fromInclusive, Instant toExclusive, CancellationToken ct = default)
        {
            return await _db.TicketOrders
                .Where(o => o.PaymentStatus == Humans.Domain.Enums.TicketPaymentStatus.Paid
                            && o.PurchasedAt >= fromInclusive
                            && o.PurchasedAt < toExclusive)
                .Select(o => o.PurchasedAt)
                .ToListAsync(ct);
        }

        // Members below are unused by the dashboard compute paths under test.
        public Task<int> GetUserTicketCountAsync(Guid userId) => throw new NotSupportedException();
        public Task<HashSet<Guid>> GetUserIdsWithTicketsAsync() => throw new NotSupportedException();
        public Task<HashSet<Guid>> GetAllMatchedUserIdsAsync() => throw new NotSupportedException();
        public Task<Humans.Application.DTOs.TicketDashboardStats> GetDashboardStatsAsync() => throw new NotSupportedException();
        public Task<decimal> GetGrossTicketRevenueAsync() => throw new NotSupportedException();
        public Task<Humans.Application.DTOs.BreakEvenResult> CalculateBreakEvenAsync(int ticketsSold, decimal grossRevenue, string currency, bool canAccessFinance, int fallbackTarget) => throw new NotSupportedException();
        public Task<Humans.Application.DTOs.TicketSalesAggregates> GetSalesAggregatesAsync() => throw new NotSupportedException();
        public Task<List<string>> GetAvailableTicketTypesAsync() => throw new NotSupportedException();
        public Task<Humans.Application.DTOs.CodeTrackingData> GetCodeTrackingDataAsync(string? search) => throw new NotSupportedException();
        public Task<Humans.Application.DTOs.OrdersPageResult> GetOrdersPageAsync(string? search, string sortBy, bool sortDesc, int page, int pageSize, string? filterPaymentStatus, string? filterTicketType, bool? filterMatched) => throw new NotSupportedException();
        public Task<Humans.Application.DTOs.AttendeesPageResult> GetAttendeesPageAsync(string? search, string sortBy, bool sortDesc, int page, int pageSize, string? filterTicketType, string? filterStatus, bool? filterMatched, string? filterOrderId) => throw new NotSupportedException();
        public Task<Humans.Application.DTOs.WhoHasntBoughtResult> GetWhoHasntBoughtAsync(string? search, string? filterTeam, string? filterTier, string? filterTicketStatus, int page, int pageSize) => throw new NotSupportedException();
        public Task<List<Humans.Application.DTOs.AttendeeExportRow>> GetAttendeeExportDataAsync() => throw new NotSupportedException();
        public Task<List<Humans.Application.DTOs.OrderExportRow>> GetOrderExportDataAsync() => throw new NotSupportedException();
        public Task<bool> HasTicketAttendeeMatchAsync(Guid userId) => throw new NotSupportedException();
        public Task<List<UserTicketOrderSummary>> GetUserTicketOrderSummariesAsync(Guid userId) => throw new NotSupportedException();
        public Task<bool> HasCurrentEventTicketAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<UserTicketExportData> GetUserTicketExportDataAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Instant?> GetPostEventHoldDateAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeUserService : IUserService
    {
        private readonly HumansDbContext _db;
        public FakeUserService(HumansDbContext db) => _db = db;

        public async Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default)
            => await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        public async Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
        {
            if (userIds.Count == 0) return new Dictionary<Guid, User>();
            var users = await _db.Users.Where(u => userIds.Contains(u.Id)).ToListAsync(ct);
            return users.ToDictionary(u => u.Id);
        }

        public async Task<IReadOnlyList<Instant>> GetLoginTimestampsInWindowAsync(Instant fromInclusive, Instant toExclusive, CancellationToken ct = default)
        {
            return await _db.Users
                .Where(u => u.LastLoginAt != null && u.LastLoginAt >= fromInclusive && u.LastLoginAt < toExclusive)
                .Select(u => u.LastLoginAt!.Value)
                .ToListAsync(ct);
        }

        // Members below are unused by the dashboard compute paths under test.
        public Task<Humans.Domain.Entities.EventParticipation?> GetParticipationAsync(Guid userId, int year, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<Humans.Domain.Entities.EventParticipation>> GetAllParticipationsForYearAsync(int year, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Humans.Domain.Entities.EventParticipation> DeclareNotAttendingAsync(Guid userId, int year, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetParticipationFromTicketSyncAsync(Guid userId, int year, Humans.Domain.Enums.ParticipationStatus status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> BackfillParticipationsAsync(int year, List<(Guid UserId, Humans.Domain.Enums.ParticipationStatus Status)> entries, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<User>> GetAllUsersAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TrySetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SetDeletionPendingAsync(Guid userId, Instant requestedAt, Instant scheduledFor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<User?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<User>> GetContactUsersAsync(string? search, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Guid?> GetOtherUserIdHavingGoogleEmailAsync(string email, Guid excludeUserId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeTeamService : ITeamService
    {
        private readonly HumansDbContext _db;
        public FakeTeamService(HumansDbContext db) => _db = db;

        public async Task<IReadOnlyDictionary<Guid, Team>> GetByIdsWithParentsAsync(IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken = default)
        {
            if (teamIds.Count == 0) return new Dictionary<Guid, Team>();
            var requested = await _db.Teams.Where(t => teamIds.Contains(t.Id)).ToListAsync(cancellationToken);
            var parentIds = requested
                .Where(t => t.ParentTeamId.HasValue)
                .Select(t => t.ParentTeamId!.Value)
                .Where(id => !teamIds.Contains(id))
                .Distinct()
                .ToList();
            var parents = parentIds.Count == 0
                ? new List<Team>()
                : await _db.Teams.Where(t => parentIds.Contains(t.Id)).ToListAsync(cancellationToken);
            var dict = new Dictionary<Guid, Team>();
            foreach (var t in requested) dict[t.Id] = t;
            foreach (var t in parents) dict[t.Id] = t;
            return dict;
        }

        public async Task<IReadOnlyList<TeamCoordinatorRef>> GetActiveCoordinatorsForTeamsAsync(IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken = default)
        {
            if (teamIds.Count == 0) return Array.Empty<TeamCoordinatorRef>();
            return await _db.TeamMembers
                .Where(m => teamIds.Contains(m.TeamId) && m.LeftAt == null && m.Role == TeamMemberRole.Coordinator)
                .Select(m => new TeamCoordinatorRef(m.TeamId, m.UserId))
                .ToListAsync(cancellationToken);
        }

        // Members below are unused by the dashboard compute paths under test.
        public Task<Team> CreateTeamAsync(string name, string? description, bool requiresApproval, Guid? parentTeamId = null, string? googleGroupPrefix = null, bool isHidden = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Team?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Team?> GetTeamByIdAsync(Guid teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetTeamNameByGoogleGroupPrefixAsync(string googleGroupPrefix, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, string>> GetTeamNamesByIdsAsync(IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Team>> GetAllTeamsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Team>> GetUserCreatedTeamsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
        public Task<IReadOnlyList<TeamJoinRequest>> GetPendingRequestsForApproverAsync(Guid approverUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamJoinRequest>> GetPendingRequestsForTeamAsync(Guid teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamJoinRequest?> GetUserPendingRequestAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> CanUserApproveRequestsForTeamAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsUserMemberOfTeamAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsUserCoordinatorOfTeamAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RemoveMemberAsync(Guid teamId, Guid userId, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamMember>> GetTeamMembersAsync(Guid teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, int>> GetPendingRequestCountsByTeamIdsAsync(IEnumerable<Guid> teamIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, string>> GetManagementRoleNamesByTeamIdsAsync(IEnumerable<Guid> teamIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, List<string>>> GetNonSystemTeamNamesByUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamOptionDto>> GetActiveTeamOptionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(IReadOnlyList<Team> Items, int TotalCount)> GetAllTeamsForAdminAsync(int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdminTeamListResult> GetAdminTeamListAsync(int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamRosterSlotSummary>> GetRosterAsync(string? priority, string? status, string? period, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamMember> AddMemberToTeamAsync(Guid teamId, Guid targetUserId, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetMemberRoleAsync(Guid teamId, Guid userId, TeamMemberRole role, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateTeamPageContentAsync(Guid teamId, string? pageContent, List<Humans.Domain.ValueObjects.CallToAction> callsToAction, bool isPublicPage, bool showCoordinatorsOnPublicPage, Guid updatedByUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamRoleDefinition> CreateRoleDefinitionAsync(Guid teamId, string name, string? description, int slotCount, List<Humans.Domain.Enums.SlotPriority> priorities, int sortOrder, Humans.Domain.Enums.RolePeriod period, Guid actorUserId, bool isPublic = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamRoleDefinition> UpdateRoleDefinitionAsync(Guid roleDefinitionId, string name, string? description, int slotCount, List<Humans.Domain.Enums.SlotPriority> priorities, int sortOrder, bool isManagement, Humans.Domain.Enums.RolePeriod period, Guid actorUserId, bool isPublic = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteRoleDefinitionAsync(Guid roleDefinitionId, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetRoleIsManagementAsync(Guid roleDefinitionId, bool isManagement, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamRoleDefinition>> GetRoleDefinitionsAsync(Guid teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamRoleDefinition>> GetAllRoleDefinitionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamRoleAssignment> AssignToRoleAsync(Guid roleDefinitionId, Guid targetUserId, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UnassignFromRoleAsync(Guid roleDefinitionId, Guid teamMemberId, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetUserCoordinatedTeamIdsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetCoordinatorUserIdsAsync(Guid teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TeamMember> AddSeededMemberAsync(Guid teamId, Guid userId, TeamMemberRole role, Instant joinedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> HardDeleteSeededTeamsAsync(string nameSuffix, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void RemoveMemberFromAllTeamsCache(Guid userId) => throw new NotSupportedException();
        public void InvalidateActiveTeamsCache() => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetActiveTeamNamesForUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnqueueGoogleResyncForUserTeamsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RevokeAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamOptionDto>> GetBudgetableTeamsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetEffectiveBudgetCoordinatorTeamIdsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetActiveMemberUserIdsAsync(Guid teamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetActiveNonSystemTeamNamesByUserIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
