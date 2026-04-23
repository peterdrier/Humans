using AwesomeAssertions;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Budget;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;
using BudgetServiceImpl = Humans.Application.Services.Budget.BudgetService;

namespace Humans.Application.Tests.Services;

public class BudgetServiceTests : IAsyncLifetime
{
    private readonly ServiceProvider _provider;
    private readonly IDbContextFactory<HumansDbContext> _factory;
    private readonly BudgetRepository _repository;
    private readonly ITeamService _teamService;
    private readonly FakeClock _clock;
    private readonly BudgetServiceImpl _service;
    private readonly Guid _yearId = Guid.NewGuid();

    public BudgetServiceTests()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<HumansDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        _provider = services.BuildServiceProvider();

        _factory = _provider.GetRequiredService<IDbContextFactory<HumansDbContext>>();
        _repository = new BudgetRepository(_factory, NullLogger<BudgetRepository>.Instance);
        _teamService = Substitute.For<ITeamService>();
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 31, 12, 0));

        _service = new BudgetServiceImpl(
            _repository,
            _teamService,
            _clock,
            NullLogger<BudgetServiceImpl>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
    }

    // ─── VAT rate validation ─────────────────────────────────────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(22)]
    public async Task CreateLineItemAsync_rejects_vat_rates_outside_0_to_21(int vatRate)
    {
        var category = await SeedCategoryAsync();

        var act = () => _service.CreateLineItemAsync(
            category.Id,
            "Test line item",
            100m,
            null,
            null,
            null,
            vatRate,
            Guid.NewGuid());

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*between 0 and 21*");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(22)]
    public async Task UpdateLineItemAsync_rejects_vat_rates_outside_0_to_21(int vatRate)
    {
        var category = await SeedCategoryAsync();
        var lineItem = new BudgetLineItem
        {
            Id = Guid.NewGuid(),
            BudgetCategoryId = category.Id,
            Description = "Existing",
            Amount = 100m,
            VatRate = 0
        };

        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            ctx.BudgetLineItems.Add(lineItem);
            await ctx.SaveChangesAsync();
        }

        var act = () => _service.UpdateLineItemAsync(
            lineItem.Id,
            "Existing",
            100m,
            null,
            null,
            null,
            vatRate,
            Guid.NewGuid());

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*between 0 and 21*");
    }

    // ─── CreateYearAsync with scaffold ──────────────────────────────────────

    [Fact]
    public async Task CreateYearAsync_seeds_department_and_ticketing_groups_atomically()
    {
        IReadOnlyList<TeamOptionDto> teams =
        [
            new(Guid.NewGuid(), "Kitchen"),
            new(Guid.NewGuid(), "Site Ops")
        ];
        _teamService.GetBudgetableTeamsAsync().Returns(teams);

        var year = await _service.CreateYearAsync("2026", "Budget 2026", Guid.NewGuid());

        year.Year.Should().Be("2026");
        year.Name.Should().Be("Budget 2026");
        year.Status.Should().Be(BudgetYearStatus.Draft);

        await using var ctx = await _factory.CreateDbContextAsync();
        var persistedYear = await ctx.BudgetYears
            .Include(y => y.Groups)
                .ThenInclude(g => g.Categories)
            .Include(y => y.Groups)
                .ThenInclude(g => g.TicketingProjection)
            .FirstAsync(y => y.Id == year.Id);

        persistedYear.Groups.Should().HaveCount(2);

        var deptGroup = persistedYear.Groups.Single(g => g.IsDepartmentGroup);
        deptGroup.Categories.Should().HaveCount(2);
        deptGroup.Categories.Select(c => c.Name).Should().BeEquivalentTo("Kitchen", "Site Ops");

        var ticketingGroup = persistedYear.Groups.Single(g => g.IsTicketingGroup);
        ticketingGroup.TicketingProjection.Should().NotBeNull();
        ticketingGroup.Categories.Select(c => c.Name).Should()
            .BeEquivalentTo("Ticket Revenue", "Processing Fees");

        // Single audit log entry for the year creation.
        var auditEntries = await ctx.BudgetAuditLogs
            .Where(a => a.BudgetYearId == year.Id)
            .ToListAsync();
        auditEntries.Should().ContainSingle()
            .Which.Description.Should().Contain("Created budget year");
    }

    // ─── UpdateYearStatusAsync auto-closes previously active years ──────────

    [Fact]
    public async Task UpdateYearStatusAsync_activating_closes_other_active_years()
    {
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = Guid.NewGuid(),
                Year = "2025",
                Name = "Budget 2025",
                Status = BudgetYearStatus.Active
            });
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Draft
            });
            await ctx.SaveChangesAsync();
        }

        await _service.UpdateYearStatusAsync(_yearId, BudgetYearStatus.Active, Guid.NewGuid());

        await using var ctx2 = await _factory.CreateDbContextAsync();
        var years = await ctx2.BudgetYears.ToListAsync();

        years.Single(y => string.Equals(y.Year, "2025", StringComparison.Ordinal)).Status
            .Should().Be(BudgetYearStatus.Closed);
        years.Single(y => string.Equals(y.Year, "2026", StringComparison.Ordinal)).Status
            .Should().Be(BudgetYearStatus.Active);

        // Both status transitions audited.
        var auditEntries = await ctx2.BudgetAuditLogs
            .Where(a => a.FieldName == nameof(BudgetYear.Status))
            .ToListAsync();
        auditEntries.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpdateYearStatusAsync_missing_year_throws()
    {
        var act = () => _service.UpdateYearStatusAsync(Guid.NewGuid(), BudgetYearStatus.Active, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    // ─── UpdateYearAsync writes field audits only for changes ────────────────

    [Fact]
    public async Task UpdateYearAsync_writes_field_audit_only_for_changed_fields()
    {
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Draft
            });
            await ctx.SaveChangesAsync();
        }

        await _service.UpdateYearAsync(_yearId, "2026", "Budget Twenty Twenty Six", Guid.NewGuid());

        await using var ctx2 = await _factory.CreateDbContextAsync();
        var auditEntries = await ctx2.BudgetAuditLogs
            .Where(a => a.BudgetYearId == _yearId)
            .ToListAsync();

        auditEntries.Should().ContainSingle(a => a.FieldName == nameof(BudgetYear.Name));
        auditEntries.Should().NotContain(a => a.FieldName == nameof(BudgetYear.Year));
    }

    // ─── DeleteYearAsync refuses active ─────────────────────────────────────

    [Fact]
    public async Task DeleteYearAsync_refuses_when_year_is_active()
    {
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Active
            });
            await ctx.SaveChangesAsync();
        }

        var act = () => _service.DeleteYearAsync(_yearId, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*active*");
    }

    [Fact]
    public async Task DeleteYearAsync_soft_deletes_when_draft()
    {
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Draft
            });
            await ctx.SaveChangesAsync();
        }

        await _service.DeleteYearAsync(_yearId, Guid.NewGuid());

        await using var ctx2 = await _factory.CreateDbContextAsync();
        var year = await ctx2.BudgetYears.SingleAsync(y => y.Id == _yearId);
        year.IsDeleted.Should().BeTrue();
        year.DeletedAt.Should().NotBeNull();
        year.Status.Should().Be(BudgetYearStatus.Closed);
    }

    // ─── Closed year blocks edits ──────────────────────────────────────────

    [Fact]
    public async Task CreateGroupAsync_refuses_when_year_is_closed()
    {
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Closed
            });
            await ctx.SaveChangesAsync();
        }

        var act = () => _service.CreateGroupAsync(_yearId, "Logistics", false, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*closed*");
    }

    // ─── SyncTicketingActuals materializes projections inside one save ─────

    [Fact]
    public async Task SyncTicketingActualsAsync_upserts_weekly_actuals_and_updates_projection_params()
    {
        var (groupId, projectionId, revenueCatId, feesCatId) = await SeedTicketingYearAsync();

        var actuals = new List<DTOs.TicketingWeeklyActuals>
        {
            new(Monday: new LocalDate(2026, 3, 2),
                Sunday: new LocalDate(2026, 3, 8),
                WeekLabel: "Mar 2–Mar 8",
                TicketCount: 10,
                Revenue: 500m,
                StripeFees: 15m,
                TicketTailorFees: 5m)
        };

        var changed = await _service.SyncTicketingActualsAsync(_yearId, actuals);

        changed.Should().BeGreaterThan(0);

        await using var ctx = await _factory.CreateDbContextAsync();
        var revenueItems = await ctx.BudgetLineItems
            .Where(li => li.BudgetCategoryId == revenueCatId)
            .ToListAsync();
        revenueItems.Should().Contain(li => li.Description.StartsWith("Week of"));

        var feeItems = await ctx.BudgetLineItems
            .Where(li => li.BudgetCategoryId == feesCatId)
            .ToListAsync();
        feeItems.Should().Contain(li => li.Description.StartsWith("Stripe fees:"));
        feeItems.Should().Contain(li => li.Description.StartsWith("TT fees:"));

        var projection = await ctx.TicketingProjections.SingleAsync(p => p.Id == projectionId);
        projection.AverageTicketPrice.Should().Be(50m); // 500 / 10
    }

    [Fact]
    public async Task SyncTicketingActualsAsync_is_noop_when_no_ticketing_group()
    {
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Draft
            });
            await ctx.SaveChangesAsync();
        }

        var result = await _service.SyncTicketingActualsAsync(
            _yearId,
            new List<DTOs.TicketingWeeklyActuals>());

        result.Should().Be(0);
    }

    [Fact]
    public async Task RefreshTicketingProjectionsAsync_materializes_projected_weeks_when_projection_is_valid()
    {
        var (groupId, _, revenueCatId, feesCatId) = await SeedTicketingYearAsync();
        await ConfigureProjectionAsync(groupId,
            startDate: new LocalDate(2026, 3, 15),
            eventDate: new LocalDate(2026, 4, 15),
            averageTicketPrice: 100m,
            dailySalesRate: 5m);

        var created = await _service.RefreshTicketingProjectionsAsync(_yearId);

        created.Should().BeGreaterThan(0);

        await using var ctx = await _factory.CreateDbContextAsync();
        var projectedRevenueItems = await ctx.BudgetLineItems
            .Where(li => li.BudgetCategoryId == revenueCatId
                && li.Description.StartsWith("Projected:"))
            .ToListAsync();
        projectedRevenueItems.Should().NotBeEmpty();
    }

    // ─── Seeding helpers ────────────────────────────────────────────────────

    private async Task<BudgetCategory> SeedCategoryAsync()
    {
        var year = new BudgetYear
        {
            Id = Guid.NewGuid(),
            Year = "2026",
            Name = "Budget 2026"
        };
        var group = new BudgetGroup
        {
            Id = Guid.NewGuid(),
            BudgetYearId = year.Id,
            BudgetYear = year,
            Name = "Departments"
        };
        var category = new BudgetCategory
        {
            Id = Guid.NewGuid(),
            BudgetGroupId = group.Id,
            BudgetGroup = group,
            Name = "Operations"
        };

        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.BudgetYears.Add(year);
        ctx.BudgetGroups.Add(group);
        ctx.BudgetCategories.Add(category);
        await ctx.SaveChangesAsync();

        return category;
    }

    private async Task<(Guid GroupId, Guid ProjectionId, Guid RevenueCatId, Guid FeesCatId)>
        SeedTicketingYearAsync()
    {
        var groupId = Guid.NewGuid();
        var projectionId = Guid.NewGuid();
        var revenueCatId = Guid.NewGuid();
        var feesCatId = Guid.NewGuid();

        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.BudgetYears.Add(new BudgetYear
        {
            Id = _yearId,
            Year = "2026",
            Name = "Budget 2026",
            Status = BudgetYearStatus.Active
        });

        ctx.BudgetGroups.Add(new BudgetGroup
        {
            Id = groupId,
            BudgetYearId = _yearId,
            Name = "Ticketing",
            IsTicketingGroup = true
        });

        ctx.BudgetCategories.Add(new BudgetCategory
        {
            Id = revenueCatId,
            BudgetGroupId = groupId,
            Name = "Ticket Revenue"
        });

        ctx.BudgetCategories.Add(new BudgetCategory
        {
            Id = feesCatId,
            BudgetGroupId = groupId,
            Name = "Processing Fees"
        });

        ctx.TicketingProjections.Add(new TicketingProjection
        {
            Id = projectionId,
            BudgetGroupId = groupId
        });

        await ctx.SaveChangesAsync();

        return (groupId, projectionId, revenueCatId, feesCatId);
    }

    private async Task ConfigureProjectionAsync(
        Guid groupId,
        LocalDate startDate,
        LocalDate eventDate,
        decimal averageTicketPrice,
        decimal dailySalesRate)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var projection = await ctx.TicketingProjections.SingleAsync(p => p.BudgetGroupId == groupId);
        projection.StartDate = startDate;
        projection.EventDate = eventDate;
        projection.AverageTicketPrice = averageTicketPrice;
        projection.DailySalesRate = dailySalesRate;
        await ctx.SaveChangesAsync();
    }
}
