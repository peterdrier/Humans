using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime.Testing;
using Humans.Teams.Contracts;
using Humans.Base.Enums;
using Humans.Budget.Domain;
using Humans.Budget.Contracts;
using Humans.Budget.Data;
using Humans.Budget.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;
using BudgetServiceImpl = Humans.Budget.Services.BudgetService;
using Humans.Users.Contracts;

namespace Humans.Budget.Tests.Services;

/// <summary>
/// Owns its fixture rather than deriving from <c>Humans.Application.Tests</c>'
/// <c>ServiceTestHarness</c>: that harness is built around an in-memory
/// <c>UsersDbContext</c> and this test only ever used two of its members — the clock and
/// the Budget section context. Inheriting it would have granted a section test project
/// <c>InternalsVisibleTo</c> on <c>UsersDbContext</c>, which is the boundary the G5 split
/// exists to draw (nobodies-collective/Humans#866).
/// </summary>
public sealed class BudgetServiceTests
{
    private readonly FakeClock Clock = new(Instant.FromUtc(2026, 3, 31, 12, 0));

    private readonly TestDbContextFactory<BudgetDbContext> BudgetDbFactory =
        new(new DbContextOptionsBuilder<BudgetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private readonly BudgetRepository _repository;
    private readonly ITeamServiceRead _teamService;
    private readonly BudgetServiceImpl _service;
    private readonly Guid _yearId = Guid.NewGuid();

    public BudgetServiceTests()
    {
        _repository = new BudgetRepository(BudgetDbFactory, NullLogger<BudgetRepository>.Instance);
        _teamService = Substitute.For<ITeamServiceRead>();
        var userService = Substitute.For<IUserService>();
        userService.GetMergedSourceIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());

        _service = new BudgetServiceImpl(
            _repository,
            _teamService,
            userService,
            Clock,
            NullLogger<BudgetServiceImpl>.Instance);
    }

    // ─── VAT rate validation ─────────────────────────────────────────────────

    [HumansTheory]
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

    [HumansFact]
    public async Task CreateLineItemAsync_PersistsLineItem()
    {
        var category = await SeedCategoryAsync();

        var created = await _service.CreateLineItemAsync(
            category.Id,
            "Test line item",
            100m,
            null,
            null,
            null,
            0,
            Guid.NewGuid());

        var persisted = await _service.GetLineItemByIdAsync(created.Id);
        persisted.Should().NotBeNull();
        persisted.Description.Should().Be("Test line item");
        persisted.Amount.Should().Be(100m);
    }

    [HumansTheory]
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

        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetLineItems.Add(lineItem);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
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

    [HumansFact]
    public async Task UpdateLineItemAsync_PersistsChanges()
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
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetLineItems.Add(lineItem);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _service.UpdateLineItemAsync(
            lineItem.Id,
            "Updated",
            150m,
            null,
            null,
            null,
            0,
            Guid.NewGuid());

        var persisted = await _service.GetLineItemByIdAsync(lineItem.Id);
        persisted.Should().NotBeNull();
        persisted.Description.Should().Be("Updated");
        persisted.Amount.Should().Be(150m);
    }

    [HumansFact]
    public async Task GetCoordinatorBudgetViewDataAsync_RedirectsNonCoordinatorNonFinanceUser()
    {
        var result = await _service.GetCoordinatorBudgetViewDataAsync(Guid.NewGuid(), isFinanceAdmin: false);

        result.ShouldRedirectToSummary.Should().BeTrue();
        result.Year.Should().BeNull();
    }

    [HumansFact]
    public async Task GetCoordinatorBudgetViewDataAsync_LoadsActiveYearForFinanceAdmin()
    {
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(
            new Dictionary<Guid, TeamInfo>());
        var year = await _service.CreateYearAsync("2026", "Budget 2026", Guid.NewGuid());
        await _service.UpdateYearStatusAsync(year.Id, BudgetYearStatus.Active, Guid.NewGuid());

        var result = await _service.GetCoordinatorBudgetViewDataAsync(Guid.NewGuid(), isFinanceAdmin: true);

        result.ShouldRedirectToSummary.Should().BeFalse();
        result.Year!.Id.Should().Be(year.Id);
        result.IsFinanceAdmin.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetCoordinatorCategoryDetailViewDataAsync_ReturnsCategoryAndTeamsForFinanceAdmin()
    {
        var category = await SeedCategoryAsync();
        var teamId = Guid.NewGuid();
        var teamInfo = new TeamInfo(
            teamId, "Kitchen", null, "kitchen",
            IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
            RequiresApproval: false, IsPublicPage: false, IsHidden: false,
            IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
            Members: []);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(
            new Dictionary<Guid, TeamInfo> { [teamId] = teamInfo });

        var result = await _service.GetCoordinatorCategoryDetailViewDataAsync(category.Id, Guid.NewGuid(), isFinanceAdmin: true);

        result.ShouldForbid.Should().BeFalse();
        result.Category!.Id.Should().Be(category.Id);
        result.Teams.Should().HaveCount(1);
        result.Teams[0].Id.Should().Be(teamId);
        result.Teams[0].Name.Should().Be("Kitchen");
    }

    [HumansFact]
    public async Task GetCoordinatorCategoryDetailViewDataAsync_ForbidsNonFinanceNonCoordinator()
    {
        var category = await SeedCategoryAsync();

        var result = await _service.GetCoordinatorCategoryDetailViewDataAsync(category.Id, Guid.NewGuid(), isFinanceAdmin: false);

        result.ShouldForbid.Should().BeTrue();
        result.Category!.Id.Should().Be(category.Id);
        result.Teams.Should().BeEmpty();
    }

    // ─── Effective budget-coordinator team set (derived over TeamInfo) ───────

    [HumansFact]
    public async Task GetEffectiveCoordinatorTeamIdsAsync_IncludesDepartmentAndActiveChildren_ForDirectCoordinator()
    {
        var userId = Guid.NewGuid();
        var deptId = Guid.NewGuid();
        var activeChildId = Guid.NewGuid();
        var inactiveChildId = Guid.NewGuid();

        var teams = new Dictionary<Guid, TeamInfo>
        {
            [deptId] = MakeTeam(
                deptId, "Kitchen", parentTeamId: null, isActive: true,
                members: [MakeMember(userId, TeamMemberRole.Coordinator)]),
            [activeChildId] = MakeTeam(
                activeChildId, "Prep", parentTeamId: deptId, isActive: true),
            [inactiveChildId] = MakeTeam(
                inactiveChildId, "Retired Sub-team", parentTeamId: deptId, isActive: false),
        };
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(teams);

        var result = await _service.GetEffectiveCoordinatorTeamIdsAsync(userId);

        result.Should().BeEquivalentTo(new[] { deptId, activeChildId });
    }

    [HumansFact]
    public async Task GetEffectiveCoordinatorTeamIdsAsync_IncludesDepartment_ForManagementRoleHolder()
    {
        var userId = Guid.NewGuid();
        var deptId = Guid.NewGuid();

        var teams = new Dictionary<Guid, TeamInfo>
        {
            [deptId] = MakeTeam(
                deptId, "Logistics", parentTeamId: null, isActive: true,
                managementRoleHolderUserIds: new HashSet<Guid> { userId }),
        };
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(teams);

        var result = await _service.GetEffectiveCoordinatorTeamIdsAsync(userId);

        result.Should().BeEquivalentTo(new[] { deptId });
    }

    [HumansFact]
    public async Task GetEffectiveCoordinatorTeamIdsAsync_ExcludesChildTeamCoordinatorship_OnlyDepartmentsQualify()
    {
        var userId = Guid.NewGuid();
        var deptId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        // User is a coordinator of a CHILD team (which has a parent), not a department.
        var teams = new Dictionary<Guid, TeamInfo>
        {
            [deptId] = MakeTeam(deptId, "Site Ops", parentTeamId: null, isActive: true),
            [childId] = MakeTeam(
                childId, "Fences", parentTeamId: deptId, isActive: true,
                members: [MakeMember(userId, TeamMemberRole.Coordinator)]),
        };
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(teams);

        var result = await _service.GetEffectiveCoordinatorTeamIdsAsync(userId);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetEffectiveCoordinatorTeamIdsAsync_ReturnsEmpty_ForNonCoordinatorMember()
    {
        var userId = Guid.NewGuid();
        var deptId = Guid.NewGuid();

        var teams = new Dictionary<Guid, TeamInfo>
        {
            [deptId] = MakeTeam(
                deptId, "Kitchen", parentTeamId: null, isActive: true,
                members: [MakeMember(userId, TeamMemberRole.Member)]),
        };
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(teams);

        var result = await _service.GetEffectiveCoordinatorTeamIdsAsync(userId);

        result.Should().BeEmpty();
    }

    private static TeamInfo MakeTeam(
        Guid id,
        string name,
        Guid? parentTeamId,
        bool isActive,
        List<TeamMemberInfo>? members = null,
        IReadOnlySet<Guid>? managementRoleHolderUserIds = null) =>
        new(
            id, name, null, name.ToLowerInvariant().Replace(' ', '-'),
            IsActive: isActive, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
            RequiresApproval: false, IsPublicPage: false, IsHidden: false,
            IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
            Members: members ?? [],
            ParentTeamId: parentTeamId,
            ManagementRoleHolderUserIds: managementRoleHolderUserIds);

    private static TeamMemberInfo MakeMember(Guid userId, TeamMemberRole role) =>
        new(
            TeamMemberId: Guid.NewGuid(),
            UserId: userId,
            DisplayName: "Member",
            Email: null,
            ProfilePictureUrl: null,
            Role: role,
            JoinedAt: Instant.MinValue);

    [HumansFact]
    public async Task CreateYearAsync_seeds_department_and_ticketing_groups_atomically()
    {
        var kitchenId = Guid.NewGuid();
        var siteOpsId = Guid.NewGuid();
        var teams = new Dictionary<Guid, TeamInfo>
        {
            [kitchenId] = new(
                kitchenId, "Kitchen", null, "kitchen",
                IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
                RequiresApproval: false, IsPublicPage: false, IsHidden: false,
                IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
                Members: [],
                HasBudget: true),
            [siteOpsId] = new(
                siteOpsId, "Site Ops", null, "site-ops",
                IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
                RequiresApproval: false, IsPublicPage: false, IsHidden: false,
                IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
                Members: [],
                HasBudget: true),
        };
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(teams);

        var year = await _service.CreateYearAsync("2026", "Budget 2026", Guid.NewGuid());

        year.Year.Should().Be("2026");
        year.Name.Should().Be("Budget 2026");
        year.Status.Should().Be(BudgetYearStatus.Draft);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var persistedYear = await ctx.BudgetYears
            .Include(y => y.Groups)
                .ThenInclude(g => g.Categories)
            .Include(y => y.Groups)
                .ThenInclude(g => g.TicketingProjection)
            .FirstAsync(y => y.Id == year.Id, TestContext.Current.CancellationToken);

        persistedYear.Groups.Should().HaveCount(2);

        var deptGroup = persistedYear.Groups.Single(g => g.IsDepartmentGroup);
        deptGroup.Categories.Should().HaveCount(2);
        deptGroup.Categories.Select(c => c.Name).Should().BeEquivalentTo("Kitchen", "Site Ops");

        var ticketingGroup = persistedYear.Groups.Single(g => g.IsTicketingGroup);
        ticketingGroup.TicketingProjection.Should().NotBeNull();
        ticketingGroup.Categories.Select(c => c.Name).Should()
            .BeEquivalentTo("Ticket Revenue", "Processing Fees");

        var auditEntries = await ctx.BudgetAuditLogs
            .Where(a => a.BudgetYearId == year.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        auditEntries.Should().ContainSingle()
            .Which.Description.Should().Contain("Created budget year");
    }

    // ─── UpdateYearStatusAsync auto-closes previously active years ──────────

    [HumansFact]
    public async Task UpdateYearStatusAsync_activating_closes_other_active_years()
    {
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
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
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _service.UpdateYearStatusAsync(_yearId, BudgetYearStatus.Active, Guid.NewGuid());

        await using var ctx2 = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var years = await ctx2.BudgetYears.ToListAsync(TestContext.Current.CancellationToken);

        years.Single(y => string.Equals(y.Year, "2025", StringComparison.Ordinal)).Status
            .Should().Be(BudgetYearStatus.Closed);
        years.Single(y => string.Equals(y.Year, "2026", StringComparison.Ordinal)).Status
            .Should().Be(BudgetYearStatus.Active);

        var auditEntries = await ctx2.BudgetAuditLogs
            .Where(a => a.FieldName == nameof(BudgetYear.Status))
            .ToListAsync(TestContext.Current.CancellationToken);
        auditEntries.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task UpdateYearStatusAsync_missing_year_throws()
    {
        var act = () => _service.UpdateYearStatusAsync(Guid.NewGuid(), BudgetYearStatus.Active, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    // ─── UpdateYearAsync writes field audits only for changes ────────────────

    [HumansFact]
    public async Task UpdateYearAsync_writes_field_audit_only_for_changed_fields()
    {
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Draft
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _service.UpdateYearAsync(_yearId, "2026", "Budget Twenty Twenty Six", Guid.NewGuid());

        await using var ctx2 = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var auditEntries = await ctx2.BudgetAuditLogs
            .Where(a => a.BudgetYearId == _yearId)
            .ToListAsync(TestContext.Current.CancellationToken);

        auditEntries.Should().ContainSingle(a => a.FieldName == nameof(BudgetYear.Name));
        auditEntries.Should().NotContain(a => a.FieldName == nameof(BudgetYear.Year));
    }

    // ─── DeleteYearAsync refuses active ─────────────────────────────────────

    [HumansFact]
    public async Task DeleteYearAsync_refuses_when_year_is_active()
    {
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Active
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var act = () => _service.DeleteYearAsync(_yearId, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*active*");
    }

    [HumansFact]
    public async Task DeleteYearAsync_soft_deletes_when_draft()
    {
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Draft
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _service.DeleteYearAsync(_yearId, Guid.NewGuid());

        await using var ctx2 = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var year = await ctx2.BudgetYears.SingleAsync(y => y.Id == _yearId, TestContext.Current.CancellationToken);
        year.IsDeleted.Should().BeTrue();
        year.DeletedAt.Should().NotBeNull();
        year.Status.Should().Be(BudgetYearStatus.Closed);
    }

    // ─── Closed year blocks edits ──────────────────────────────────────────

    [HumansFact]
    public async Task CreateGroupAsync_refuses_when_year_is_closed()
    {
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Closed
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var act = () => _service.CreateGroupAsync(_yearId, "Logistics", false, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*closed*");
    }

    // ─── SyncTicketingActuals materializes projections inside one save ─────

    [HumansFact]
    public async Task SyncTicketingActualsAsync_upserts_weekly_actuals_and_updates_projection_params()
    {
        var (_, projectionId, revenueCatId, feesCatId) = await SeedTicketingYearAsync();

        var actuals = new List<TicketingWeeklyActuals>
        {
            new(Monday: new LocalDate(2026, 3, 2),
                Sunday: new LocalDate(2026, 3, 8),
                WeekLabel: "Mar 2–Mar 8",
                TicketCount: 10,
                Revenue: 500m,
                StripeFees: 15m,
                TicketTailorFees: 5m)
        };

        var changed = await _service.SyncTicketingActualsAsync(_yearId, actuals, TestContext.Current.CancellationToken);

        changed.Should().BeGreaterThan(0);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var revenueItems = await ctx.BudgetLineItems
            .Where(li => li.BudgetCategoryId == revenueCatId)
            .ToListAsync(TestContext.Current.CancellationToken);
        revenueItems.Should().Contain(li => li.Description.StartsWith("Week of"));

        var feeItems = await ctx.BudgetLineItems
            .Where(li => li.BudgetCategoryId == feesCatId)
            .ToListAsync(TestContext.Current.CancellationToken);
        feeItems.Should().Contain(li => li.Description.StartsWith("Stripe fees:"));
        feeItems.Should().Contain(li => li.Description.StartsWith("TT fees:"));

        var projection = await ctx.TicketingProjections.SingleAsync(p => p.Id == projectionId, TestContext.Current.CancellationToken);
        projection.AverageTicketPrice.Should().Be(50m); // 500 / 10
    }

    [HumansFact]
    public async Task SyncTicketingActualsAsync_is_noop_when_no_ticketing_group()
    {
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetYears.Add(new BudgetYear
            {
                Id = _yearId,
                Year = "2026",
                Name = "Budget 2026",
                Status = BudgetYearStatus.Draft
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await _service.SyncTicketingActualsAsync(
            _yearId,
            new List<TicketingWeeklyActuals>(), TestContext.Current.CancellationToken);

        result.Should().Be(0);
    }

    [HumansFact]
    public async Task RefreshTicketingProjectionsAsync_materializes_projected_weeks_when_projection_is_valid()
    {
        var (groupId, _, revenueCatId, _) = await SeedTicketingYearAsync();
        await ConfigureProjectionAsync(groupId,
            startDate: new LocalDate(2026, 3, 15),
            eventDate: new LocalDate(2026, 4, 15),
            averageTicketPrice: 100m,
            dailySalesRate: 5m);

        var created = await _service.RefreshTicketingProjectionsAsync(_yearId, TestContext.Current.CancellationToken);

        created.Should().BeGreaterThan(0);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var projectedRevenueItems = await ctx.BudgetLineItems
            .Where(li => li.BudgetCategoryId == revenueCatId
                && li.Description.StartsWith("Projected:"))
            .ToListAsync(TestContext.Current.CancellationToken);
        projectedRevenueItems.Should().NotBeEmpty();
    }

    // Guards the ordering invariant: materialization runs in the repo AFTER
    // UpdateProjectionFromActuals, so projected items use the newly-learned
    // average price / fee percentages rather than the pre-sync ones.
    [HumansFact]
    public async Task SyncTicketingActualsAsync_projected_items_use_post_update_avg_price_not_pre_sync_value()
    {
        var (groupId, _, revenueCatId, _) = await SeedTicketingYearAsync();

        // Projection configured with AverageTicketPrice=100. After the sync,
        // actuals (20 tickets, 1000 revenue) will re-learn AverageTicketPrice=50.
        // The Projected: revenue line items must use 50, not 100.
        await ConfigureProjectionAsync(groupId,
            startDate: new LocalDate(2026, 4, 6), // Monday after the FakeClock today (2026-03-31).
            eventDate: new LocalDate(2026, 5, 4),
            averageTicketPrice: 100m,
            dailySalesRate: 5m);

        var actuals = new List<TicketingWeeklyActuals>
        {
            new(Monday: new LocalDate(2026, 3, 16),
                Sunday: new LocalDate(2026, 3, 22),
                WeekLabel: "Mar 16–Mar 22",
                TicketCount: 20,
                Revenue: 1000m,
                StripeFees: 0m,
                TicketTailorFees: 0m)
        };

        await _service.SyncTicketingActualsAsync(_yearId, actuals, TestContext.Current.CancellationToken);

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);

        // The projection was updated in-place before materialization.
        var projection = await ctx.TicketingProjections.SingleAsync(p => p.BudgetGroupId == groupId, TestContext.Current.CancellationToken);
        projection.AverageTicketPrice.Should().Be(50m);

        // Projected: revenue items must reflect the new AvgPrice of 50.
        // Given 5 tickets/day * 7 days = 35 tickets/week at 50 = 1750/week.
        // Assert divisibility by 50 rather than exact totals: independent of
        // initial-burst math.
        var projectedItems = await ctx.BudgetLineItems
            .Where(li => li.BudgetCategoryId == revenueCatId
                && li.Description.StartsWith("Projected:"))
            .ToListAsync(TestContext.Current.CancellationToken);

        projectedItems.Should().NotBeEmpty("projection is valid and covers multiple weeks before event");

        foreach (var item in projectedItems)
        {
            // Revenue = tickets * 50 (new price). If stale 100 was used, it'd be tickets * 100.
            // Parse ticket count from notes "~N tickets" and verify amount == count * 50.
            item.Notes.Should().NotBeNullOrEmpty();
            var notesClean = item.Notes!.TrimStart('~');
            var spaceIdx = notesClean.IndexOf(' ', StringComparison.Ordinal);
            spaceIdx.Should().BeGreaterThan(0);
            var ticketCount = int.Parse(
                notesClean[..spaceIdx],
                System.Globalization.CultureInfo.InvariantCulture);

            item.Amount.Should().Be(
                ticketCount * 50m,
                because: $"projected revenue must use post-sync learned price (50), not pre-sync value (100); item '{item.Description}' had {ticketCount} tickets");
        }
    }

    // ─── Sync never touches hand-entered items ──────────────────────────────

    [HumansFact]
    public async Task SyncTicketingActualsAsync_never_touches_hand_entered_line_items()
    {
        var (groupId, _, revenueCatId, _) = await SeedTicketingYearAsync();
        await ConfigureProjectionAsync(groupId,
            startDate: new LocalDate(2026, 4, 6),
            eventDate: new LocalDate(2026, 5, 4),
            averageTicketPrice: 50m,
            dailySalesRate: 5m);

        var manualId = Guid.NewGuid();
        var manualProjectedId = Guid.NewGuid();
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetLineItems.Add(new BudgetLineItem
            {
                Id = manualId,
                BudgetCategoryId = revenueCatId,
                Description = "Vendor deposit",
                Amount = 123.45m,
                Notes = "hand-entered",
                IsAutoGenerated = false
            });
            // Hand-entered item that happens to carry the sweep prefix: only
            // IsAutoGenerated items may be removed or upserted by the sync.
            ctx.BudgetLineItems.Add(new BudgetLineItem
            {
                Id = manualProjectedId,
                BudgetCategoryId = revenueCatId,
                Description = "Projected: manual note",
                Amount = -10m,
                IsAutoGenerated = false
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var actuals = new List<TicketingWeeklyActuals>
        {
            new(Monday: new LocalDate(2026, 3, 16),
                Sunday: new LocalDate(2026, 3, 22),
                WeekLabel: "Mar 16-Mar 22",
                TicketCount: 20,
                Revenue: 1000m,
                StripeFees: 15m,
                TicketTailorFees: 30m)
        };

        await _service.SyncTicketingActualsAsync(_yearId, actuals, TestContext.Current.CancellationToken);

        await using var verify = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var manual = await verify.BudgetLineItems.SingleAsync(li => li.Id == manualId, TestContext.Current.CancellationToken);
        manual.Description.Should().Be("Vendor deposit");
        manual.Amount.Should().Be(123.45m);
        manual.Notes.Should().Be("hand-entered");
        manual.IsAutoGenerated.Should().BeFalse();

        var manualProjected = await verify.BudgetLineItems.SingleAsync(li => li.Id == manualProjectedId, TestContext.Current.CancellationToken);
        manualProjected.Amount.Should().Be(-10m);
        manualProjected.IsAutoGenerated.Should().BeFalse();

        (await verify.BudgetLineItems.AnyAsync(li => li.IsAutoGenerated, TestContext.Current.CancellationToken))
            .Should().BeTrue(because: "the sync must still materialize its own auto items alongside");
    }

    // ─── Summary computation ────────────────────────────────────────────────

    [HumansFact]
    public void ComputeBudgetSummary_excludes_cashflow_only_and_breaks_out_vat()
    {
        var groupId = Guid.NewGuid();
        var catSales = new BudgetCategoryDetail(Guid.NewGuid(), groupId, "Sales", 0m, ExpenditureType.OpEx, null, 0,
        [
            new BudgetLineItemDetail(Guid.NewGuid(), Guid.NewGuid(), "Ticket income", 110m, null, null, new LocalDate(2026, 5, 1), 10, false, false, 0)
        ]);
        var catOps = new BudgetCategoryDetail(Guid.NewGuid(), groupId, "Ops", 0m, ExpenditureType.OpEx, null, 0,
        [
            new BudgetLineItemDetail(Guid.NewGuid(), Guid.NewGuid(), "Equipment", -121m, null, null, new LocalDate(2026, 5, 1), 21, false, false, 0)
        ]);
        var catDonations = new BudgetCategoryDetail(Guid.NewGuid(), groupId, "Donations", 0m, ExpenditureType.OpEx, null, 0,
        [
            new BudgetLineItemDetail(Guid.NewGuid(), Guid.NewGuid(), "Donations", 999m, null, null, null, 0, false, true, 0)
        ]);
        IReadOnlyList<BudgetGroupDetail> groups =
        [
            new(groupId, Guid.NewGuid(), "Main", 0, false, false, false, null, [catSales, catOps, catDonations])
        ];

        var summary = _service.ComputeBudgetSummary(groups);

        // VAT-inclusive amounts: the 110 income at 10% carries 10 of VAT (a liability);
        // the 121 expense at 21% carries 21 (a credit). The 999 cashflow-only item is out.
        summary.TotalIncome.Should().Be(131m);
        summary.TotalExpenses.Should().Be(-131m);
        summary.NetBalance.Should().Be(0m);
        summary.IncomeSlices.Select(sl => sl.Name).Should().BeEquivalentTo(["Sales", "VAT Credits"]);
        summary.ExpenseSlices.Select(sl => sl.Name).Should().BeEquivalentTo(["Ops", "VAT Liability"]);
    }

    // ─── Closed year gates every tree mutation ──────────────────────────────

    public static TheoryData<string> ClosedYearMutations => new()
    {
        "create-group", "update-group", "delete-group",
        "create-category", "update-category", "delete-category",
        "create-line-item", "update-line-item", "delete-line-item",
        "sync-departments", "ensure-ticketing-group", "update-ticketing-projection"
    };

    [HumansTheory]
    [MemberData(nameof(ClosedYearMutations))]
    public async Task Repository_mutations_refuse_when_year_is_closed(string mutation)
    {
        // One closed year carrying a normal group + category + line item and a
        // ticketing group with a projection row, so every mutation has a target.
        var groupId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var lineItemId = Guid.NewGuid();
        var ticketingGroupId = Guid.NewGuid();
        await using (var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            ctx.BudgetYears.Add(new BudgetYear { Id = _yearId, Year = "2026", Name = "Budget 2026", Status = BudgetYearStatus.Closed });
            ctx.BudgetGroups.Add(new BudgetGroup { Id = groupId, BudgetYearId = _yearId, Name = "Departments" });
            ctx.BudgetCategories.Add(new BudgetCategory { Id = categoryId, BudgetGroupId = groupId, Name = "Operations" });
            ctx.BudgetLineItems.Add(new BudgetLineItem { Id = lineItemId, BudgetCategoryId = categoryId, Description = "Rent", Amount = -1m });
            ctx.BudgetGroups.Add(new BudgetGroup { Id = ticketingGroupId, BudgetYearId = _yearId, Name = "Ticketing", IsTicketingGroup = true });
            ctx.TicketingProjections.Add(new TicketingProjection { Id = Guid.NewGuid(), BudgetGroupId = ticketingGroupId });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var actor = Guid.NewGuid();
        var now = Clock.GetCurrentInstant();
        var ct = TestContext.Current.CancellationToken;
        Func<Task> act = mutation switch
        {
            "create-group" => () => _repository.CreateGroupAsync(_yearId, "New", false, actor, now, ct),
            "update-group" => () => _repository.UpdateGroupAsync(groupId, "Renamed", 1, false, actor, now, ct),
            "delete-group" => () => _repository.DeleteGroupAsync(groupId, actor, now, ct),
            "create-category" => () => _repository.CreateCategoryAsync(groupId, "New", 0m, ExpenditureType.OpEx, null, actor, now, ct),
            "update-category" => () => _repository.UpdateCategoryAsync(categoryId, "Renamed", 1m, ExpenditureType.OpEx, actor, now, ct),
            "delete-category" => () => _repository.DeleteCategoryAsync(categoryId, actor, now, ct),
            "create-line-item" => () => _repository.CreateLineItemAsync(new BudgetLineItemDraft(categoryId, "New", 1m, null, null, null, 0), actor, now, ct),
            "update-line-item" => () => _repository.UpdateLineItemAsync(new BudgetLineItemUpdate(lineItemId, "Renamed", 2m, null, null, null, 0), actor, now, ct),
            "delete-line-item" => () => _repository.DeleteLineItemAsync(lineItemId, actor, now, ct),
            "sync-departments" => () => _repository.SyncDepartmentCategoriesAsync(_yearId, [new BudgetableTeamRef(Guid.NewGuid(), "Team")], actor, now, ct),
            "ensure-ticketing-group" => () => _repository.EnsureTicketingGroupAsync(_yearId, actor, now, ct),
            "update-ticketing-projection" => () => _repository.UpdateTicketingProjectionAsync(new TicketingProjectionUpdate(ticketingGroupId, null, null, 0, 0m, 0m, 10, 0m, 0m, 0m), actor, now, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null)
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*closed*");
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

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        ctx.BudgetYears.Add(year);
        ctx.BudgetGroups.Add(group);
        ctx.BudgetCategories.Add(category);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return category;
    }

    private async Task<(Guid GroupId, Guid ProjectionId, Guid RevenueCatId, Guid FeesCatId)>
        SeedTicketingYearAsync()
    {
        var groupId = Guid.NewGuid();
        var projectionId = Guid.NewGuid();
        var revenueCatId = Guid.NewGuid();
        var feesCatId = Guid.NewGuid();

        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
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

        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (groupId, projectionId, revenueCatId, feesCatId);
    }

    private async Task ConfigureProjectionAsync(
        Guid groupId,
        LocalDate startDate,
        LocalDate eventDate,
        decimal averageTicketPrice,
        decimal dailySalesRate)
    {
        await using var ctx = await BudgetDbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var projection = await ctx.TicketingProjections.SingleAsync(p => p.BudgetGroupId == groupId, TestContext.Current.CancellationToken);
        projection.StartDate = startDate;
        projection.EventDate = eventDate;
        projection.AverageTicketPrice = averageTicketPrice;
        projection.DailySalesRate = dailySalesRate;
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
