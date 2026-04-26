using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Budget;

/// <summary>
/// EF-backed implementation of <see cref="IBudgetRepository"/>. The only
/// non-test file that touches <c>DbContext.BudgetYears</c>,
/// <c>DbContext.BudgetGroups</c>, <c>DbContext.BudgetCategories</c>,
/// <c>DbContext.BudgetLineItems</c>, <c>DbContext.TicketingProjections</c>,
/// or <c>DbContext.BudgetAuditLogs</c>.
/// </summary>
/// <remarks>
/// Follows design-rules §15b: registered as Singleton, injects
/// <see cref="IDbContextFactory{TContext}"/>, and opens a fresh short-lived
/// <see cref="HumansDbContext"/> per method. There is no shared unit of
/// work across methods — each public method is an atomic load-mutate-save
/// against its own context. Multi-entity mutations that must be atomic
/// (e.g., creating a year + its default groups + categories) happen inside a
/// single repository method so they commit together.
/// </remarks>
public sealed class BudgetRepository : IBudgetRepository
{
    private const string TicketRevenueCategoryName = "Ticket Revenue";
    private const string ProcessingFeesCategoryName = "Processing Fees";
    private const string DepartmentsGroupName = "Departments";
    private const string TicketingGroupName = "Ticketing";
    private const string TicketingProjectedPrefix = "Projected: ";

    private readonly IDbContextFactory<HumansDbContext> _factory;
    private readonly ILogger<BudgetRepository> _logger;

    public BudgetRepository(
        IDbContextFactory<HumansDbContext> factory,
        ILogger<BudgetRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // ==========================================================================
    // Budget Years — reads
    // ==========================================================================

    public async Task<IReadOnlyList<BudgetYear>> GetAllYearsAsync(
        bool includeArchived, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var query = ctx.BudgetYears
            .AsNoTracking()
            .Include(y => y.Groups)
                .ThenInclude(g => g.Categories)
            .AsQueryable();

        if (!includeArchived)
            query = query.Where(y => !y.IsDeleted);

        return await query
            .OrderByDescending(y => y.Year)
            .ToListAsync(ct);
    }

    public async Task<BudgetYear?> GetYearByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // No cross-domain Includes — BudgetCategory.Team is an obsolete nav; views
        // render team info via the ResponsibleTeam nav on line items (owned by the
        // Teams section, read in-domain until the full strip) and via TeamId-keyed
        // lookups elsewhere.
        return await ctx.BudgetYears
            .AsNoTracking()
            .Include(y => y.Groups.OrderBy(g => g.SortOrder))
                .ThenInclude(g => g.Categories.OrderBy(c => c.SortOrder))
                    .ThenInclude(c => c.LineItems.OrderBy(li => li.SortOrder))
            .Include(y => y.Groups)
                .ThenInclude(g => g.TicketingProjection)
            .FirstOrDefaultAsync(y => y.Id == id, ct);
    }

    public async Task<BudgetYear?> GetActiveYearAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var activeId = await ctx.BudgetYears
            .AsNoTracking()
            .Where(y => y.Status == BudgetYearStatus.Active && !y.IsDeleted)
            .OrderBy(y => y.Id)
            .Select(y => (Guid?)y.Id)
            .FirstOrDefaultAsync(ct);

        if (activeId is null)
            return null;

        return await ctx.BudgetYears
            .AsNoTracking()
            .Include(y => y.Groups.OrderBy(g => g.SortOrder))
                .ThenInclude(g => g.Categories.OrderBy(c => c.SortOrder))
                    .ThenInclude(c => c.LineItems.OrderBy(li => li.SortOrder))
            .Include(y => y.Groups)
                .ThenInclude(g => g.TicketingProjection)
            .FirstOrDefaultAsync(y => y.Id == activeId.Value, ct);
    }

    public async Task<bool> IsYearClosedAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var status = await ctx.BudgetYears
            .AsNoTracking()
            .Where(y => y.Id == id)
            .Select(y => (BudgetYearStatus?)y.Status)
            .FirstOrDefaultAsync(ct);

        return status == BudgetYearStatus.Closed;
    }

    // ==========================================================================
    // Budget Years — atomic mutations
    // ==========================================================================

    public async Task CreateYearWithScaffoldAsync(
        BudgetYearDraft draft, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var budgetYear = new BudgetYear
        {
            Id = draft.Id,
            Year = draft.Year,
            Name = draft.Name,
            Status = BudgetYearStatus.Draft,
            CreatedAt = draft.Now,
            UpdatedAt = draft.Now
        };

        ctx.BudgetYears.Add(budgetYear);

        // Auto-create "Departments" group.
        var departmentGroup = new BudgetGroup
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYear.Id,
            Name = DepartmentsGroupName,
            SortOrder = 0,
            IsRestricted = false,
            IsDepartmentGroup = true,
            CreatedAt = draft.Now,
            UpdatedAt = draft.Now
        };
        ctx.BudgetGroups.Add(departmentGroup);

        // Auto-create categories for teams with HasBudget.
        var deptSortOrder = 0;
        foreach (var team in draft.BudgetableTeams)
        {
            ctx.BudgetCategories.Add(new BudgetCategory
            {
                Id = Guid.NewGuid(),
                BudgetGroupId = departmentGroup.Id,
                Name = team.Name,
                AllocatedAmount = 0,
                ExpenditureType = ExpenditureType.OpEx,
                TeamId = team.Id,
                SortOrder = deptSortOrder++,
                CreatedAt = draft.Now,
                UpdatedAt = draft.Now
            });
        }

        // Auto-create "Ticketing" group + projection + default categories.
        var ticketingGroup = new BudgetGroup
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYear.Id,
            Name = TicketingGroupName,
            SortOrder = 1,
            IsRestricted = false,
            IsTicketingGroup = true,
            CreatedAt = draft.Now,
            UpdatedAt = draft.Now
        };
        ctx.BudgetGroups.Add(ticketingGroup);

        ctx.TicketingProjections.Add(new TicketingProjection
        {
            Id = Guid.NewGuid(),
            BudgetGroupId = ticketingGroup.Id,
            CreatedAt = draft.Now,
            UpdatedAt = draft.Now
        });

        var ticketingCategories = new[]
        {
            (Name: TicketRevenueCategoryName, Sort: 0),
            (Name: ProcessingFeesCategoryName, Sort: 1)
        };

        foreach (var (name, sort) in ticketingCategories)
        {
            ctx.BudgetCategories.Add(new BudgetCategory
            {
                Id = Guid.NewGuid(),
                BudgetGroupId = ticketingGroup.Id,
                Name = name,
                AllocatedAmount = 0,
                ExpenditureType = ExpenditureType.OpEx,
                SortOrder = sort,
                CreatedAt = draft.Now,
                UpdatedAt = draft.Now
            });
        }

        AddDescriptionAudit(ctx, budgetYear.Id, nameof(BudgetYear), budgetYear.Id,
            $"Created budget year '{draft.Name}' ({draft.Year})",
            draft.ActorUserId, draft.Now);

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdateYearAsync(
        Guid yearId,
        string year,
        string name,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var budgetYear = await ctx.BudgetYears.FirstOrDefaultAsync(y => y.Id == yearId, ct);
        if (budgetYear is null)
            return false;

        if (!string.Equals(budgetYear.Year, year, StringComparison.Ordinal))
        {
            AddFieldAudit(ctx, budgetYear.Id, nameof(BudgetYear), budgetYear.Id,
                nameof(BudgetYear.Year), budgetYear.Year, year,
                actorUserId, now);
            budgetYear.Year = year;
        }

        if (!string.Equals(budgetYear.Name, name, StringComparison.Ordinal))
        {
            AddFieldAudit(ctx, budgetYear.Id, nameof(BudgetYear), budgetYear.Id,
                nameof(BudgetYear.Name), budgetYear.Name, name,
                actorUserId, now);
            budgetYear.Name = name;
        }

        budgetYear.UpdatedAt = now;

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UpdateYearStatusAsync(
        Guid yearId,
        BudgetYearStatus status,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var year = await ctx.BudgetYears.FirstOrDefaultAsync(y => y.Id == yearId, ct);
        if (year is null)
            return false;

        var oldStatus = year.Status;

        // When activating, auto-close any currently active year.
        if (status == BudgetYearStatus.Active)
        {
            var currentlyActive = await ctx.BudgetYears
                .Where(y => y.Status == BudgetYearStatus.Active && y.Id != yearId)
                .ToListAsync(ct);

            foreach (var active in currentlyActive)
            {
                active.Status = BudgetYearStatus.Closed;
                active.UpdatedAt = now;

                AddFieldAudit(ctx, active.Id, nameof(BudgetYear), active.Id,
                    nameof(BudgetYear.Status),
                    BudgetYearStatus.Active.ToString(),
                    BudgetYearStatus.Closed.ToString(),
                    actorUserId, now);
            }
        }

        year.Status = status;
        year.UpdatedAt = now;

        AddFieldAudit(ctx, year.Id, nameof(BudgetYear), year.Id,
            nameof(BudgetYear.Status), oldStatus.ToString(), status.ToString(),
            actorUserId, now);

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SoftDeleteYearAsync(
        Guid yearId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var year = await ctx.BudgetYears.FirstOrDefaultAsync(y => y.Id == yearId, ct);
        if (year is null)
            return false;

        if (year.Status == BudgetYearStatus.Active)
            throw new InvalidOperationException("Cannot delete an active budget year. Close it first.");

        year.IsDeleted = true;
        year.DeletedAt = now;
        year.Status = BudgetYearStatus.Closed;
        year.UpdatedAt = now;

        AddDescriptionAudit(ctx, year.Id, nameof(BudgetYear), year.Id,
            $"Archived budget year '{year.Name}' ({year.Year})",
            actorUserId, now);

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RestoreYearAsync(
        Guid yearId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var year = await ctx.BudgetYears.FirstOrDefaultAsync(y => y.Id == yearId, ct);
        if (year is null || !year.IsDeleted)
            return false;

        year.IsDeleted = false;
        year.DeletedAt = null;
        year.Status = BudgetYearStatus.Draft;
        year.UpdatedAt = now;

        AddDescriptionAudit(ctx, year.Id, nameof(BudgetYear), year.Id,
            $"Restored budget year '{year.Name}' ({year.Year})",
            actorUserId, now);

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> SyncDepartmentCategoriesAsync(
        Guid budgetYearId,
        IReadOnlyList<BudgetableTeamRef> budgetableTeams,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        await EnsureYearNotClosedAsync(ctx, budgetYearId, ct);

        var deptGroup = await ctx.BudgetGroups
            .Include(g => g.Categories)
            .OrderBy(g => g.Id)
            .FirstOrDefaultAsync(g => g.BudgetYearId == budgetYearId && g.IsDepartmentGroup, ct)
            ?? throw new InvalidOperationException("No Departments group found for this budget year");

        var existingTeamIds = deptGroup.Categories
            .Where(c => c.TeamId.HasValue)
            .Select(c => c.TeamId!.Value)
            .ToHashSet();

        var newTeams = budgetableTeams
            .Where(t => !existingTeamIds.Contains(t.Id))
            .ToList();

        if (newTeams.Count == 0)
            return 0;

        var maxSortOrder = deptGroup.Categories.Any()
            ? deptGroup.Categories.Max(c => c.SortOrder)
            : -1;

        foreach (var team in newTeams)
        {
            var category = new BudgetCategory
            {
                Id = Guid.NewGuid(),
                BudgetGroupId = deptGroup.Id,
                Name = team.Name,
                AllocatedAmount = 0,
                ExpenditureType = ExpenditureType.OpEx,
                TeamId = team.Id,
                SortOrder = ++maxSortOrder,
                CreatedAt = now,
                UpdatedAt = now
            };
            ctx.BudgetCategories.Add(category);

            AddDescriptionAudit(ctx, budgetYearId, nameof(BudgetCategory), category.Id,
                $"Synced department '{team.Name}' into budget",
                actorUserId, now);
        }

        await ctx.SaveChangesAsync(ct);
        return newTeams.Count;
    }

    public async Task<bool> EnsureTicketingGroupAsync(
        Guid budgetYearId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        await EnsureYearNotClosedAsync(ctx, budgetYearId, ct);

        var alreadyExists = await ctx.BudgetGroups
            .AnyAsync(g => g.BudgetYearId == budgetYearId && g.IsTicketingGroup, ct);
        if (alreadyExists)
            return false;

        var maxSortOrder = await ctx.BudgetGroups
            .Where(g => g.BudgetYearId == budgetYearId)
            .MaxAsync(g => (int?)g.SortOrder, ct) ?? -1;

        var ticketingGroup = new BudgetGroup
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYearId,
            Name = TicketingGroupName,
            SortOrder = maxSortOrder + 1,
            IsRestricted = false,
            IsTicketingGroup = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        ctx.BudgetGroups.Add(ticketingGroup);

        ctx.TicketingProjections.Add(new TicketingProjection
        {
            Id = Guid.NewGuid(),
            BudgetGroupId = ticketingGroup.Id,
            CreatedAt = now,
            UpdatedAt = now
        });

        var ticketingCategories = new[]
        {
            (Name: TicketRevenueCategoryName, Sort: 0),
            (Name: ProcessingFeesCategoryName, Sort: 1)
        };

        foreach (var (name, sort) in ticketingCategories)
        {
            ctx.BudgetCategories.Add(new BudgetCategory
            {
                Id = Guid.NewGuid(),
                BudgetGroupId = ticketingGroup.Id,
                Name = name,
                AllocatedAmount = 0,
                ExpenditureType = ExpenditureType.OpEx,
                SortOrder = sort,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        AddDescriptionAudit(ctx, budgetYearId, nameof(BudgetGroup), ticketingGroup.Id,
            "Added Ticketing group with projection parameters",
            actorUserId, now);

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    // ==========================================================================
    // Budget Groups — atomic mutations
    // ==========================================================================

    public async Task<BudgetGroup> CreateGroupAsync(
        Guid budgetYearId,
        string name,
        string slug,
        bool isRestricted,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var yearExists = await ctx.BudgetYears.AnyAsync(y => y.Id == budgetYearId, ct);
        if (!yearExists)
            throw new InvalidOperationException($"Budget year {budgetYearId} not found");

        await EnsureYearNotClosedAsync(ctx, budgetYearId, ct);

        var maxSortOrder = await ctx.BudgetGroups
            .Where(g => g.BudgetYearId == budgetYearId)
            .MaxAsync(g => (int?)g.SortOrder, ct) ?? -1;

        var group = new BudgetGroup
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYearId,
            Name = name,
            Slug = slug,
            SortOrder = maxSortOrder + 1,
            IsRestricted = isRestricted,
            IsDepartmentGroup = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        ctx.BudgetGroups.Add(group);

        AddDescriptionAudit(ctx, budgetYearId, nameof(BudgetGroup), group.Id,
            $"Created budget group '{name}'",
            actorUserId, now);

        await ctx.SaveChangesAsync(ct);
        return group;
    }

    public async Task<bool> UpdateGroupAsync(
        Guid groupId,
        string name,
        string slug,
        int sortOrder,
        bool isRestricted,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var group = await ctx.BudgetGroups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
            return false;

        await EnsureYearNotClosedAsync(ctx, group.BudgetYearId, ct);

        if (!string.Equals(group.Name, name, StringComparison.Ordinal))
        {
            AddFieldAudit(ctx, group.BudgetYearId, nameof(BudgetGroup), group.Id,
                nameof(BudgetGroup.Name), group.Name, name,
                actorUserId, now);
            group.Name = name;
        }

        if (!string.Equals(group.Slug, slug, StringComparison.Ordinal))
        {
            AddFieldAudit(ctx, group.BudgetYearId, nameof(BudgetGroup), group.Id,
                nameof(BudgetGroup.Slug), group.Slug, slug,
                actorUserId, now);
            group.Slug = slug;
        }

        if (group.SortOrder != sortOrder)
        {
            AddFieldAudit(ctx, group.BudgetYearId, nameof(BudgetGroup), group.Id,
                nameof(BudgetGroup.SortOrder),
                group.SortOrder.ToString(CultureInfo.InvariantCulture),
                sortOrder.ToString(CultureInfo.InvariantCulture),
                actorUserId, now);
            group.SortOrder = sortOrder;
        }

        if (group.IsRestricted != isRestricted)
        {
            AddFieldAudit(ctx, group.BudgetYearId, nameof(BudgetGroup), group.Id,
                nameof(BudgetGroup.IsRestricted),
                group.IsRestricted.ToString(), isRestricted.ToString(),
                actorUserId, now);
            group.IsRestricted = isRestricted;
        }

        group.UpdatedAt = now;

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteGroupAsync(
        Guid groupId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var group = await ctx.BudgetGroups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
            return false;

        await EnsureYearNotClosedAsync(ctx, group.BudgetYearId, ct);

        if (group.IsDepartmentGroup)
            throw new InvalidOperationException("Cannot delete the auto-generated Departments group.");

        AddDescriptionAudit(ctx, group.BudgetYearId, nameof(BudgetGroup), group.Id,
            $"Deleted budget group '{group.Name}'",
            actorUserId, now);

        ctx.BudgetGroups.Remove(group);

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    // ==========================================================================
    // Budget Categories — reads
    // ==========================================================================

    public async Task<BudgetCategory?> GetCategoryByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // BudgetCategory.Team is an obsolete cross-domain nav — callers resolve
        // team names via ITeamService keyed off TeamId. BudgetLineItem.ResponsibleTeam
        // is still used by the Finance CategoryDetail view and will be stripped in
        // a follow-up; keep the Include until that PR lands.
        return await ctx.BudgetCategories
            .AsNoTracking()
            .Include(c => c.BudgetGroup)
                .ThenInclude(g => g!.BudgetYear)
            .Include(c => c.LineItems.OrderBy(li => li.SortOrder))
                .ThenInclude(li => li.ResponsibleTeam)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<BudgetCategory?> GetCategoryBySlugAsync(
        Guid budgetYearId, string groupSlug, string categorySlug, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.BudgetCategories
            .AsNoTracking()
            .Where(c => c.Slug == categorySlug
                        && c.BudgetGroup!.Slug == groupSlug
                        && c.BudgetGroup.BudgetYearId == budgetYearId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<BudgetYear?> GetYearForDateAsync(LocalDate date, CancellationToken ct = default)
    {
        // BudgetYear.Year is a string per the existing model (e.g., "2026").
        // Match by calendar year-of-date; if no year row carries that string, return null.
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var yearStr = date.Year.ToString(CultureInfo.InvariantCulture);
        return await ctx.BudgetYears
            .AsNoTracking()
            .Where(y => !y.IsDeleted && y.Year == yearStr)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<BudgetCategory>> GetCategoriesByYearAsync(
        Guid budgetYearId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.BudgetCategories
            .AsNoTracking()
            .Where(c => c.BudgetGroup!.BudgetYearId == budgetYearId)
            .Include(c => c.BudgetGroup)
            .OrderBy(c => c.BudgetGroup!.SortOrder).ThenBy(c => c.SortOrder)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<(BudgetYear Year, BudgetGroup Group, BudgetCategory Category)>> GetTagInventoryRowsAsync(
        Guid budgetYearId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.BudgetCategories
            .AsNoTracking()
            .Where(c => c.BudgetGroup!.BudgetYearId == budgetYearId)
            .Select(c => new
            {
                Year = c.BudgetGroup!.BudgetYear,
                Group = c.BudgetGroup,
                Category = c
            })
            .ToListAsync(ct);
        return rows.Select(r => (r.Year!, r.Group, r.Category)).ToList();
    }

    // ==========================================================================
    // Budget Categories — atomic mutations
    // ==========================================================================

    public async Task<BudgetCategory> CreateCategoryAsync(
        Guid budgetGroupId,
        string name,
        string slug,
        decimal allocatedAmount,
        ExpenditureType expenditureType,
        Guid? teamId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var group = await ctx.BudgetGroups.FirstOrDefaultAsync(g => g.Id == budgetGroupId, ct)
            ?? throw new InvalidOperationException($"Budget group {budgetGroupId} not found");

        await EnsureYearNotClosedAsync(ctx, group.BudgetYearId, ct);

        var maxSortOrder = await ctx.BudgetCategories
            .Where(c => c.BudgetGroupId == budgetGroupId)
            .MaxAsync(c => (int?)c.SortOrder, ct) ?? -1;

        var category = new BudgetCategory
        {
            Id = Guid.NewGuid(),
            BudgetGroupId = budgetGroupId,
            Name = name,
            Slug = slug,
            AllocatedAmount = allocatedAmount,
            ExpenditureType = expenditureType,
            TeamId = teamId,
            SortOrder = maxSortOrder + 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        ctx.BudgetCategories.Add(category);

        AddDescriptionAudit(ctx, group.BudgetYearId, nameof(BudgetCategory), category.Id,
            $"Created budget category '{name}' with allocation {allocatedAmount:N2}",
            actorUserId, now);

        await ctx.SaveChangesAsync(ct);
        return category;
    }

    public async Task<bool> UpdateCategoryAsync(
        Guid categoryId,
        string name,
        string slug,
        decimal allocatedAmount,
        ExpenditureType expenditureType,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var category = await ctx.BudgetCategories
            .Include(c => c.BudgetGroup)
            .FirstOrDefaultAsync(c => c.Id == categoryId, ct);
        if (category is null)
            return false;

        var budgetYearId = category.BudgetGroup!.BudgetYearId;
        await EnsureYearNotClosedAsync(ctx, budgetYearId, ct);

        if (!string.Equals(category.Name, name, StringComparison.Ordinal))
        {
            AddFieldAudit(ctx, budgetYearId, nameof(BudgetCategory), category.Id,
                nameof(BudgetCategory.Name), category.Name, name,
                actorUserId, now);
            category.Name = name;
        }

        if (!string.Equals(category.Slug, slug, StringComparison.Ordinal))
        {
            AddFieldAudit(ctx, budgetYearId, nameof(BudgetCategory), category.Id,
                nameof(BudgetCategory.Slug), category.Slug, slug,
                actorUserId, now);
            category.Slug = slug;
        }

        if (category.AllocatedAmount != allocatedAmount)
        {
            AddFieldAudit(ctx, budgetYearId, nameof(BudgetCategory), category.Id,
                nameof(BudgetCategory.AllocatedAmount),
                category.AllocatedAmount.ToString("N2", CultureInfo.InvariantCulture),
                allocatedAmount.ToString("N2", CultureInfo.InvariantCulture),
                actorUserId, now);
            category.AllocatedAmount = allocatedAmount;
        }

        if (category.ExpenditureType != expenditureType)
        {
            AddFieldAudit(ctx, budgetYearId, nameof(BudgetCategory), category.Id,
                nameof(BudgetCategory.ExpenditureType),
                category.ExpenditureType.ToString(), expenditureType.ToString(),
                actorUserId, now);
            category.ExpenditureType = expenditureType;
        }

        category.UpdatedAt = now;

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteCategoryAsync(
        Guid categoryId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var category = await ctx.BudgetCategories
            .Include(c => c.BudgetGroup)
            .FirstOrDefaultAsync(c => c.Id == categoryId, ct);
        if (category is null)
            return false;

        var budgetYearId = category.BudgetGroup!.BudgetYearId;
        await EnsureYearNotClosedAsync(ctx, budgetYearId, ct);

        AddDescriptionAudit(ctx, budgetYearId, nameof(BudgetCategory), category.Id,
            $"Deleted budget category '{category.Name}'",
            actorUserId, now);

        ctx.BudgetCategories.Remove(category);

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    // ==========================================================================
    // Budget Line Items — reads
    // ==========================================================================

    public async Task<BudgetLineItem?> GetLineItemByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        return await ctx.BudgetLineItems
            .AsNoTracking()
            .FirstOrDefaultAsync(li => li.Id == id, ct);
    }

    // ==========================================================================
    // Budget Line Items — atomic mutations
    // ==========================================================================

    public async Task<BudgetLineItem> CreateLineItemAsync(
        BudgetLineItemDraft draft,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var category = await ctx.BudgetCategories
            .Include(c => c.BudgetGroup)
            .FirstOrDefaultAsync(c => c.Id == draft.BudgetCategoryId, ct)
            ?? throw new InvalidOperationException($"Budget category {draft.BudgetCategoryId} not found");

        var budgetYearId = category.BudgetGroup!.BudgetYearId;
        await EnsureYearNotClosedAsync(ctx, budgetYearId, ct);

        var maxSortOrder = await ctx.BudgetLineItems
            .Where(li => li.BudgetCategoryId == draft.BudgetCategoryId)
            .MaxAsync(li => (int?)li.SortOrder, ct) ?? -1;

        var lineItem = new BudgetLineItem
        {
            Id = Guid.NewGuid(),
            BudgetCategoryId = draft.BudgetCategoryId,
            Description = draft.Description,
            Amount = draft.Amount,
            ResponsibleTeamId = draft.ResponsibleTeamId,
            Notes = draft.Notes,
            ExpectedDate = draft.ExpectedDate,
            VatRate = draft.VatRate,
            SortOrder = maxSortOrder + 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        ctx.BudgetLineItems.Add(lineItem);

        AddDescriptionAudit(ctx, budgetYearId, nameof(BudgetLineItem), lineItem.Id,
            $"Created line item '{draft.Description}' ({draft.Amount:N2})",
            actorUserId, now);

        await ctx.SaveChangesAsync(ct);
        return lineItem;
    }

    public async Task<bool> UpdateLineItemAsync(
        BudgetLineItemUpdate update,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var lineItem = await ctx.BudgetLineItems
            .Include(li => li.BudgetCategory)
                .ThenInclude(c => c!.BudgetGroup)
            .FirstOrDefaultAsync(li => li.Id == update.LineItemId, ct);
        if (lineItem is null)
            return false;

        var budgetYearId = lineItem.BudgetCategory!.BudgetGroup!.BudgetYearId;
        await EnsureYearNotClosedAsync(ctx, budgetYearId, ct);

        if (!string.Equals(lineItem.Description, update.Description, StringComparison.Ordinal))
        {
            AddFieldAudit(ctx, budgetYearId, nameof(BudgetLineItem), lineItem.Id,
                nameof(BudgetLineItem.Description),
                lineItem.Description, update.Description,
                actorUserId, now);
            lineItem.Description = update.Description;
        }

        if (lineItem.Amount != update.Amount)
        {
            AddFieldAudit(ctx, budgetYearId, nameof(BudgetLineItem), lineItem.Id,
                nameof(BudgetLineItem.Amount),
                lineItem.Amount.ToString("N2", CultureInfo.InvariantCulture),
                update.Amount.ToString("N2", CultureInfo.InvariantCulture),
                actorUserId, now);
            lineItem.Amount = update.Amount;
        }

        if (lineItem.ResponsibleTeamId != update.ResponsibleTeamId)
        {
            AddFieldAudit(ctx, budgetYearId, nameof(BudgetLineItem), lineItem.Id,
                nameof(BudgetLineItem.ResponsibleTeamId),
                lineItem.ResponsibleTeamId?.ToString(),
                update.ResponsibleTeamId?.ToString(),
                actorUserId, now);
            lineItem.ResponsibleTeamId = update.ResponsibleTeamId;
        }

        if (!string.Equals(lineItem.Notes, update.Notes, StringComparison.Ordinal))
        {
            AddFieldAudit(ctx, budgetYearId, nameof(BudgetLineItem), lineItem.Id,
                nameof(BudgetLineItem.Notes),
                lineItem.Notes, update.Notes,
                actorUserId, now);
            lineItem.Notes = update.Notes;
        }

        if (lineItem.ExpectedDate != update.ExpectedDate)
        {
            AddFieldAudit(ctx, budgetYearId, nameof(BudgetLineItem), lineItem.Id,
                nameof(BudgetLineItem.ExpectedDate),
                lineItem.ExpectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                update.ExpectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                actorUserId, now);
            lineItem.ExpectedDate = update.ExpectedDate;
        }

        if (lineItem.VatRate != update.VatRate)
        {
            AddFieldAudit(ctx, budgetYearId, nameof(BudgetLineItem), lineItem.Id,
                nameof(BudgetLineItem.VatRate),
                lineItem.VatRate.ToString(CultureInfo.InvariantCulture),
                update.VatRate.ToString(CultureInfo.InvariantCulture),
                actorUserId, now);
            lineItem.VatRate = update.VatRate;
        }

        lineItem.UpdatedAt = now;

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteLineItemAsync(
        Guid lineItemId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var lineItem = await ctx.BudgetLineItems
            .Include(li => li.BudgetCategory)
                .ThenInclude(c => c!.BudgetGroup)
            .FirstOrDefaultAsync(li => li.Id == lineItemId, ct);
        if (lineItem is null)
            return false;

        var budgetYearId = lineItem.BudgetCategory!.BudgetGroup!.BudgetYearId;
        await EnsureYearNotClosedAsync(ctx, budgetYearId, ct);

        AddDescriptionAudit(ctx, budgetYearId, nameof(BudgetLineItem), lineItem.Id,
            $"Deleted line item '{lineItem.Description}'",
            actorUserId, now);

        ctx.BudgetLineItems.Remove(lineItem);

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    // ==========================================================================
    // Ticketing Projection — reads
    // ==========================================================================

    public async Task<TicketingProjection?> GetTicketingProjectionAsync(
        Guid budgetGroupId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        return await ctx.TicketingProjections
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.BudgetGroupId == budgetGroupId, ct);
    }

    public async Task<BudgetGroup?> GetGroupByIdAsync(Guid groupId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        return await ctx.BudgetGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == groupId, ct);
    }

    // ==========================================================================
    // Ticketing Projection — atomic mutations
    // ==========================================================================

    public async Task<bool> UpdateTicketingProjectionAsync(
        TicketingProjectionUpdate update,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var group = await ctx.BudgetGroups.FirstOrDefaultAsync(g => g.Id == update.BudgetGroupId, ct)
            ?? throw new InvalidOperationException($"Budget group {update.BudgetGroupId} not found");

        if (!group.IsTicketingGroup)
            throw new InvalidOperationException("Projection parameters can only be set on ticketing groups.");

        await EnsureYearNotClosedAsync(ctx, group.BudgetYearId, ct);

        var projection = await ctx.TicketingProjections
            .FirstOrDefaultAsync(p => p.BudgetGroupId == update.BudgetGroupId, ct)
            ?? throw new InvalidOperationException("No ticketing projection found for this group.");

        projection.StartDate = update.StartDate;
        projection.EventDate = update.EventDate;
        projection.InitialSalesCount = update.InitialSalesCount;
        projection.DailySalesRate = update.DailySalesRate;
        projection.AverageTicketPrice = update.AverageTicketPrice;
        projection.VatRate = update.VatRate;
        projection.StripeFeePercent = update.StripeFeePercent;
        projection.StripeFeeFixed = update.StripeFeeFixed;
        projection.TicketTailorFeePercent = update.TicketTailorFeePercent;
        projection.UpdatedAt = now;

        AddDescriptionAudit(ctx, group.BudgetYearId, nameof(TicketingProjection), projection.Id,
            "Updated ticketing projection parameters",
            actorUserId, now);

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> SyncTicketingActualsAsync(
        Guid budgetYearId,
        IReadOnlyList<TicketingWeeklyActualsInput> weeklyActuals,
        LocalDate today,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var ticketingGroup = await LoadTicketingGroupForMutationAsync(ctx, budgetYearId, ct);
        if (ticketingGroup is null)
            return 0;

        var revenueCategory = ticketingGroup.Categories.FirstOrDefault(
            c => string.Equals(c.Name, TicketRevenueCategoryName, StringComparison.Ordinal));
        var feesCategory = ticketingGroup.Categories.FirstOrDefault(
            c => string.Equals(c.Name, ProcessingFeesCategoryName, StringComparison.Ordinal));

        if (revenueCategory is null || feesCategory is null)
        {
            _logger.LogWarning(
                "Ticketing group missing expected categories for year {YearId}", budgetYearId);
            return 0;
        }

        var projectionVatRate = ticketingGroup.TicketingProjection?.VatRate ?? 0;
        var lineItemsChanged = 0;

        foreach (var week in weeklyActuals)
        {
            lineItemsChanged += UpsertTicketingLineItem(ctx, revenueCategory,
                $"Week of {week.WeekLabel}",
                week.Revenue, week.Monday, projectionVatRate, false, $"{week.TicketCount} tickets", now);

            if (week.StripeFees > 0)
                lineItemsChanged += UpsertTicketingLineItem(ctx, feesCategory,
                    $"Stripe fees: {week.WeekLabel}",
                    -week.StripeFees, week.Monday, TicketingFeeVatRate, false, null, now);

            if (week.TicketTailorFees > 0)
                lineItemsChanged += UpsertTicketingLineItem(ctx, feesCategory,
                    $"TT fees: {week.WeekLabel}",
                    -week.TicketTailorFees, week.Monday, TicketingFeeVatRate, false, null, now);
        }

        // Refresh the projection's learned parameters from the new actuals
        // BEFORE materializing the projected-week schedule, so projected
        // items reflect the updated average price / fee percentages in the
        // same atomic op rather than lagging one sync behind.
        if (weeklyActuals.Count > 0 && ticketingGroup.TicketingProjection is not null)
        {
            var totalRevenue = weeklyActuals.Sum(w => w.Revenue);
            var totalStripeFees = weeklyActuals.Sum(w => w.StripeFees);
            var totalTtFees = weeklyActuals.Sum(w => w.TicketTailorFees);
            var totalTickets = weeklyActuals.Sum(w => w.TicketCount);

            UpdateProjectionFromActuals(
                ticketingGroup.TicketingProjection,
                totalRevenue, totalStripeFees, totalTtFees, totalTickets, now);
        }

        lineItemsChanged += MaterializeProjectedWeeks(
            ctx, ticketingGroup.TicketingProjection, revenueCategory, feesCategory, today, now);

        if (ctx.ChangeTracker.HasChanges())
            await ctx.SaveChangesAsync(ct);

        return lineItemsChanged;
    }

    public async Task<int> RefreshTicketingProjectionsAsync(
        Guid budgetYearId,
        LocalDate today,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var ticketingGroup = await LoadTicketingGroupForMutationAsync(ctx, budgetYearId, ct);
        if (ticketingGroup is null)
            return 0;

        var revenueCategory = ticketingGroup.Categories.FirstOrDefault(
            c => string.Equals(c.Name, TicketRevenueCategoryName, StringComparison.Ordinal));
        var feesCategory = ticketingGroup.Categories.FirstOrDefault(
            c => string.Equals(c.Name, ProcessingFeesCategoryName, StringComparison.Ordinal));

        if (revenueCategory is null || feesCategory is null)
            return 0;

        var created = MaterializeProjectedWeeks(
            ctx, ticketingGroup.TicketingProjection, revenueCategory, feesCategory, today, now);

        if (ctx.ChangeTracker.HasChanges())
            await ctx.SaveChangesAsync(ct);

        return created;
    }

    // ==========================================================================
    // Audit Log — reads (append-only per §12)
    // ==========================================================================

    public async Task<IReadOnlyList<BudgetAuditLog>> GetAuditLogAsync(
        Guid? budgetYearId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // No cross-domain Include — BudgetAuditLog.ActorUser is obsolete; the
        // Finance audit log view renders actor via <human-link user-id=@ActorUserId>.
        var query = ctx.BudgetAuditLogs.AsNoTracking().AsQueryable();

        if (budgetYearId.HasValue)
            query = query.Where(a => a.BudgetYearId == budgetYearId.Value);

        return await query
            .OrderByDescending(a => a.OccurredAt)
            .Take(500)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BudgetAuditLog>> GetAuditLogEntriesForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        return await ctx.BudgetAuditLogs
            .AsNoTracking()
            .Where(bal => bal.ActorUserId == userId)
            .OrderByDescending(bal => bal.OccurredAt)
            .ToListAsync(ct);
    }

    // ==========================================================================
    // Private helpers
    // ==========================================================================

    // Spanish IVA rate applied to Stripe and TicketTailor processing fees.
    private const int TicketingFeeVatRate = 21;

    private static async Task EnsureYearNotClosedAsync(
        HumansDbContext ctx, Guid budgetYearId, CancellationToken ct)
    {
        var status = await ctx.BudgetYears
            .AsNoTracking()
            .Where(y => y.Id == budgetYearId)
            .Select(y => (BudgetYearStatus?)y.Status)
            .FirstOrDefaultAsync(ct);

        if (status == BudgetYearStatus.Closed)
            throw new InvalidOperationException("Cannot modify a closed budget year.");
    }

    private static async Task<BudgetGroup?> LoadTicketingGroupForMutationAsync(
        HumansDbContext ctx, Guid budgetYearId, CancellationToken ct)
    {
        return await ctx.BudgetGroups
            .Include(g => g.Categories)
                .ThenInclude(c => c.LineItems)
            .Include(g => g.TicketingProjection)
            .FirstOrDefaultAsync(g => g.BudgetYearId == budgetYearId && g.IsTicketingGroup, ct);
    }

    private static void UpdateProjectionFromActuals(
        TicketingProjection projection,
        decimal totalRevenue, decimal totalStripeFees, decimal totalTtFees, int totalTickets, Instant now)
    {
        if (totalTickets > 0)
            projection.AverageTicketPrice = Math.Round(totalRevenue / totalTickets, 2);

        if (totalRevenue > 0)
        {
            projection.StripeFeePercent = Math.Round(totalStripeFees / totalRevenue * 100m, 2);
            projection.TicketTailorFeePercent = Math.Round(totalTtFees / totalRevenue * 100m, 2);
        }

        projection.UpdatedAt = now;
    }

    /// <summary>
    /// Removes stale projected line items and re-materializes the projected-
    /// week schedule from the <em>current</em> <see cref="TicketingProjection"/>
    /// state inside this <c>DbContext</c>. Critically, this runs AFTER the
    /// caller has updated projection parameters from actuals (so the
    /// Projected: items reflect the new average price / fee percentages
    /// rather than the pre-sync values).
    /// </summary>
    private static int MaterializeProjectedWeeks(
        HumansDbContext ctx,
        TicketingProjection? projection,
        BudgetCategory revenueCategory,
        BudgetCategory feesCategory,
        LocalDate today,
        Instant now)
    {
        // Always clear stale projected items first (stable behavior whether the projection is valid or not).
        RemoveProjectedItems(ctx, revenueCategory);
        RemoveProjectedItems(ctx, feesCategory);

        if (projection is null
            || projection.StartDate is null
            || projection.EventDate is null
            || projection.AverageTicketPrice == 0)
        {
            return 0;
        }

        var currentWeekMonday = GetTicketingIsoMonday(today);
        var eventDate = projection.EventDate.Value;

        var projectionStart = currentWeekMonday > projection.StartDate.Value
            ? currentWeekMonday
            : GetTicketingIsoMonday(projection.StartDate.Value);

        if (projectionStart >= eventDate)
            return 0;

        var dailyRate = projection.DailySalesRate;
        var initialBurst = projection.InitialSalesCount;
        var isFirstWeek = true;
        var created = 0;
        var weekStart = projectionStart;

        while (weekStart < eventDate)
        {
            var weekEnd = weekStart.PlusDays(6);
            if (weekEnd > eventDate) weekEnd = eventDate;

            var daysInWeek = Period.Between(weekStart, weekEnd.PlusDays(1), PeriodUnits.Days).Days;

            var weekTickets = (int)Math.Round(dailyRate * daysInWeek);
            if (isFirstWeek && projectionStart <= projection.StartDate.Value)
            {
                weekTickets += initialBurst;
                isFirstWeek = false;
            }
            else
            {
                isFirstWeek = false;
            }

            if (weekTickets <= 0) weekTickets = 1;

            var weekRevenue = weekTickets * projection.AverageTicketPrice;
            var stripeFees = weekRevenue * projection.StripeFeePercent / 100m
                + weekTickets * projection.StripeFeeFixed;
            var ttFees = weekRevenue * projection.TicketTailorFeePercent / 100m;

            var weekLabel = FormatTicketingWeekLabel(weekStart, weekEnd);

            created += UpsertTicketingLineItem(ctx, revenueCategory,
                $"{TicketingProjectedPrefix}Week of {weekLabel}",
                Math.Round(weekRevenue, 2), weekStart, projection.VatRate, false,
                $"~{weekTickets} tickets", now);

            if (stripeFees > 0)
                created += UpsertTicketingLineItem(ctx, feesCategory,
                    $"{TicketingProjectedPrefix}Stripe fees: {weekLabel}",
                    -Math.Round(stripeFees, 2), weekStart, TicketingFeeVatRate, false, null, now);

            if (ttFees > 0)
                created += UpsertTicketingLineItem(ctx, feesCategory,
                    $"{TicketingProjectedPrefix}TT fees: {weekLabel}",
                    -Math.Round(ttFees, 2), weekStart, TicketingFeeVatRate, false, null, now);

            weekStart = weekEnd.PlusDays(1);
            weekStart = GetTicketingIsoMonday(weekStart);
            if (weekStart <= weekEnd) weekStart = weekEnd.PlusDays(1);
        }

        return created;
    }

    private static LocalDate GetTicketingIsoMonday(LocalDate date)
    {
        // NodaTime IsoDayOfWeek: Monday=1, Sunday=7.
        var dayOfWeek = (int)date.DayOfWeek;
        return date.PlusDays(-(dayOfWeek - 1));
    }

    private static string FormatTicketingWeekLabel(LocalDate monday, LocalDate sunday)
    {
        return $"{monday.ToString("MMM d", null)}–{sunday.ToString("MMM d", null)}";
    }

    private static void RemoveProjectedItems(HumansDbContext ctx, BudgetCategory category)
    {
        var projected = category.LineItems
            .Where(li => li.IsAutoGenerated
                && li.Description.StartsWith(TicketingProjectedPrefix, StringComparison.Ordinal))
            .ToList();

        foreach (var item in projected)
        {
            category.LineItems.Remove(item);
            ctx.BudgetLineItems.Remove(item);
        }
    }

    /// <summary>
    /// Upserts an auto-generated line item by (category, description) match.
    /// Returns 1 if the item was created or mutated, 0 if unchanged.
    /// </summary>
    private static int UpsertTicketingLineItem(
        HumansDbContext ctx,
        BudgetCategory category,
        string description,
        decimal amount,
        LocalDate expectedDate,
        int vatRate,
        bool isCashflowOnly,
        string? notes,
        Instant now)
    {
        var existing = category.LineItems.FirstOrDefault(li =>
            li.IsAutoGenerated
            && string.Equals(li.Description, description, StringComparison.Ordinal));

        if (existing is not null)
        {
            if (existing.Amount == amount
                && existing.VatRate == vatRate
                && string.Equals(existing.Notes, notes, StringComparison.Ordinal))
            {
                return 0;
            }

            existing.Amount = amount;
            existing.VatRate = vatRate;
            existing.Notes = notes;
            existing.ExpectedDate = expectedDate;
            existing.UpdatedAt = now;
            return 1;
        }

        var maxSort = category.LineItems.Any() ? category.LineItems.Max(li => li.SortOrder) : -1;
        var lineItem = new BudgetLineItem
        {
            Id = Guid.NewGuid(),
            BudgetCategoryId = category.Id,
            Description = description,
            Amount = amount,
            ExpectedDate = expectedDate,
            VatRate = vatRate,
            IsAutoGenerated = true,
            IsCashflowOnly = isCashflowOnly,
            Notes = notes,
            SortOrder = maxSort + 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        ctx.BudgetLineItems.Add(lineItem);
        category.LineItems.Add(lineItem);
        return 1;
    }

    private void AddFieldAudit(
        HumansDbContext ctx,
        Guid budgetYearId, string entityType, Guid entityId,
        string fieldName, string? oldValue, string? newValue,
        Guid actorUserId, Instant occurredAt)
    {
        var description = $"Changed {entityType}.{fieldName} from '{oldValue}' to '{newValue}'";
        ctx.BudgetAuditLogs.Add(new BudgetAuditLog
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYearId,
            EntityType = entityType,
            EntityId = entityId,
            FieldName = fieldName,
            OldValue = oldValue,
            NewValue = newValue,
            Description = description,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt
        });

        // Downgraded from Information→Debug per design-rules (the audit row in
        // budget_audit_logs is the durable record; the log line is trace only).
        _logger.LogDebug(
            "BudgetAudit: {EntityType} {EntityId} — {Description} by user {ActorUserId}",
            entityType, entityId, description, actorUserId);
    }

    private void AddDescriptionAudit(
        HumansDbContext ctx,
        Guid budgetYearId, string entityType, Guid entityId,
        string description,
        Guid actorUserId, Instant occurredAt)
    {
        ctx.BudgetAuditLogs.Add(new BudgetAuditLog
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYearId,
            EntityType = entityType,
            EntityId = entityId,
            FieldName = null,
            OldValue = null,
            NewValue = null,
            Description = description,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt
        });

        _logger.LogDebug(
            "BudgetAudit: {EntityType} {EntityId} — {Description} by user {ActorUserId}",
            entityType, entityId, description, actorUserId);
    }
}
