using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IBudgetRepository"/>. The only
/// non-test file that touches <c>DbContext.BudgetYears</c>,
/// <c>DbContext.BudgetGroups</c>, <c>DbContext.BudgetCategories</c>,
/// <c>DbContext.BudgetLineItems</c>, <c>DbContext.TicketingProjections</c>,
/// or <c>DbContext.BudgetAuditLogs</c> after the Budget migration lands.
/// </summary>
public sealed class BudgetRepository : IBudgetRepository
{
    private readonly HumansDbContext _dbContext;

    public BudgetRepository(HumansDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // ==========================================================================
    // Budget Years
    // ==========================================================================

    public async Task<IReadOnlyList<BudgetYear>> GetAllYearsAsync(
        bool includeArchived, CancellationToken ct = default)
    {
        var query = _dbContext.BudgetYears
            .Include(y => y.Groups)
                .ThenInclude(g => g.Categories)
            .AsQueryable();

        if (!includeArchived)
            query = query.Where(y => !y.IsDeleted);

        return await query
            .OrderByDescending(y => y.Year)
            .ToListAsync(ct);
    }

    public Task<BudgetYear?> GetYearByIdAsync(Guid id, CancellationToken ct = default)
    {
        // No cross-domain Includes — BudgetCategory.Team is an obsolete nav; the
        // Finance/Budget views render team info via the ResponsibleTeam nav on
        // line items (owned by the Teams section, still read in-domain until the
        // full strip) and via TeamId-keyed lookups elsewhere.
        return _dbContext.BudgetYears
            .Include(y => y.Groups.OrderBy(g => g.SortOrder))
                .ThenInclude(g => g.Categories.OrderBy(c => c.SortOrder))
                    .ThenInclude(c => c.LineItems.OrderBy(li => li.SortOrder))
            .Include(y => y.Groups)
                .ThenInclude(g => g.TicketingProjection)
            .FirstOrDefaultAsync(y => y.Id == id, ct);
    }

    public async Task<BudgetYear?> GetActiveYearAsync(CancellationToken ct = default)
    {
        var active = await _dbContext.BudgetYears
            .OrderBy(y => y.Id)
            .FirstOrDefaultAsync(
                y => y.Status == BudgetYearStatus.Active && !y.IsDeleted, ct);

        if (active is null)
            return null;

        return await GetYearByIdAsync(active.Id, ct);
    }

    public Task<BudgetYear?> FindYearForMutationAsync(Guid id, CancellationToken ct = default)
    {
        return _dbContext.BudgetYears.FindAsync([id], ct).AsTask();
    }

    public async Task<IReadOnlyList<BudgetYear>> FindActiveYearsExcludingAsync(
        Guid excludingId, CancellationToken ct = default)
    {
        return await _dbContext.BudgetYears
            .Where(y => y.Status == BudgetYearStatus.Active && y.Id != excludingId)
            .ToListAsync(ct);
    }

    public async Task<bool> IsYearClosedAsync(Guid id, CancellationToken ct = default)
    {
        var status = await _dbContext.BudgetYears
            .AsNoTracking()
            .Where(y => y.Id == id)
            .Select(y => (BudgetYearStatus?)y.Status)
            .FirstOrDefaultAsync(ct);

        return status == BudgetYearStatus.Closed;
    }

    public void AddYear(BudgetYear year)
    {
        _dbContext.BudgetYears.Add(year);
    }

    // ==========================================================================
    // Budget Groups
    // ==========================================================================

    public Task<BudgetGroup?> FindGroupForMutationAsync(Guid groupId, CancellationToken ct = default)
    {
        return _dbContext.BudgetGroups.FindAsync([groupId], ct).AsTask();
    }

    public Task<BudgetGroup?> GetDepartmentGroupForMutationAsync(
        Guid budgetYearId, CancellationToken ct = default)
    {
        return _dbContext.BudgetGroups
            .Include(g => g.Categories)
            .OrderBy(g => g.Id)
            .FirstOrDefaultAsync(
                g => g.BudgetYearId == budgetYearId && g.IsDepartmentGroup, ct);
    }

    public Task<bool> HasTicketingGroupAsync(Guid budgetYearId, CancellationToken ct = default)
    {
        return _dbContext.BudgetGroups
            .AnyAsync(g => g.BudgetYearId == budgetYearId && g.IsTicketingGroup, ct);
    }

    public async Task<int> GetMaxGroupSortOrderAsync(Guid budgetYearId, CancellationToken ct = default)
    {
        return await _dbContext.BudgetGroups
            .Where(g => g.BudgetYearId == budgetYearId)
            .MaxAsync(g => (int?)g.SortOrder, ct) ?? -1;
    }

    public void AddGroup(BudgetGroup group)
    {
        _dbContext.BudgetGroups.Add(group);
    }

    public void RemoveGroup(BudgetGroup group)
    {
        _dbContext.BudgetGroups.Remove(group);
    }

    // ==========================================================================
    // Budget Categories
    // ==========================================================================

    public Task<BudgetCategory?> GetCategoryByIdAsync(Guid id, CancellationToken ct = default)
    {
        // BudgetCategory.Team is an obsolete cross-domain nav — callers resolve
        // team names via ITeamService keyed off TeamId. BudgetLineItem.ResponsibleTeam
        // is still used by the Finance CategoryDetail view and will be stripped in
        // a follow-up; keep the Include until that PR lands.
        return _dbContext.BudgetCategories
            .Include(c => c.BudgetGroup)
                .ThenInclude(g => g!.BudgetYear)
            .Include(c => c.LineItems.OrderBy(li => li.SortOrder))
                .ThenInclude(li => li.ResponsibleTeam)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public Task<BudgetCategory?> FindCategoryForMutationAsync(
        Guid categoryId, CancellationToken ct = default)
    {
        return _dbContext.BudgetCategories
            .Include(c => c.BudgetGroup)
            .FirstOrDefaultAsync(c => c.Id == categoryId, ct);
    }

    public async Task<int> GetMaxCategorySortOrderAsync(
        Guid budgetGroupId, CancellationToken ct = default)
    {
        return await _dbContext.BudgetCategories
            .Where(c => c.BudgetGroupId == budgetGroupId)
            .MaxAsync(c => (int?)c.SortOrder, ct) ?? -1;
    }

    public void AddCategory(BudgetCategory category)
    {
        _dbContext.BudgetCategories.Add(category);
    }

    public void RemoveCategory(BudgetCategory category)
    {
        _dbContext.BudgetCategories.Remove(category);
    }

    // ==========================================================================
    // Budget Line Items
    // ==========================================================================

    public Task<BudgetLineItem?> GetLineItemByIdAsync(Guid id, CancellationToken ct = default)
    {
        return _dbContext.BudgetLineItems.FindAsync([id], ct).AsTask();
    }

    public Task<BudgetLineItem?> FindLineItemForMutationAsync(
        Guid lineItemId, CancellationToken ct = default)
    {
        return _dbContext.BudgetLineItems
            .Include(li => li.BudgetCategory)
                .ThenInclude(c => c!.BudgetGroup)
            .FirstOrDefaultAsync(li => li.Id == lineItemId, ct);
    }

    public async Task<int> GetMaxLineItemSortOrderAsync(
        Guid budgetCategoryId, CancellationToken ct = default)
    {
        return await _dbContext.BudgetLineItems
            .Where(li => li.BudgetCategoryId == budgetCategoryId)
            .MaxAsync(li => (int?)li.SortOrder, ct) ?? -1;
    }

    public void AddLineItem(BudgetLineItem lineItem)
    {
        _dbContext.BudgetLineItems.Add(lineItem);
    }

    public void RemoveLineItem(BudgetLineItem lineItem)
    {
        _dbContext.BudgetLineItems.Remove(lineItem);
    }

    // ==========================================================================
    // Ticketing Projection
    // ==========================================================================

    public void AddTicketingProjection(TicketingProjection projection)
    {
        _dbContext.TicketingProjections.Add(projection);
    }

    public Task<TicketingProjection?> GetTicketingProjectionAsync(
        Guid budgetGroupId, CancellationToken ct = default)
    {
        return _dbContext.TicketingProjections
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.BudgetGroupId == budgetGroupId, ct);
    }

    public Task<TicketingProjection?> FindTicketingProjectionForMutationAsync(
        Guid budgetGroupId, CancellationToken ct = default)
    {
        return _dbContext.TicketingProjections
            .FirstOrDefaultAsync(p => p.BudgetGroupId == budgetGroupId, ct);
    }

    public Task<BudgetGroup?> GetTicketingGroupForMutationAsync(
        Guid budgetYearId, CancellationToken ct = default)
    {
        return _dbContext.BudgetGroups
            .Include(g => g.Categories)
                .ThenInclude(c => c.LineItems)
            .Include(g => g.TicketingProjection)
            .FirstOrDefaultAsync(
                g => g.BudgetYearId == budgetYearId && g.IsTicketingGroup, ct);
    }

    // ==========================================================================
    // Audit Log (append-only)
    // ==========================================================================

    public void AddAuditLog(BudgetAuditLog entry)
    {
        _dbContext.BudgetAuditLogs.Add(entry);
    }

    public async Task<IReadOnlyList<BudgetAuditLog>> GetAuditLogAsync(
        Guid? budgetYearId, CancellationToken ct = default)
    {
        // No cross-domain Include — BudgetAuditLog.ActorUser is obsolete; the
        // Finance audit log view renders actor via <human-link user-id=@ActorUserId>.
        var query = _dbContext.BudgetAuditLogs.AsQueryable();

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
        return await _dbContext.BudgetAuditLogs
            .AsNoTracking()
            .Where(bal => bal.ActorUserId == userId)
            .OrderByDescending(bal => bal.OccurredAt)
            .ToListAsync(ct);
    }

    // ==========================================================================
    // Unit-of-work
    // ==========================================================================

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _dbContext.SaveChangesAsync(ct);
    }

    public bool HasPendingChanges() => _dbContext.ChangeTracker.HasChanges();
}
