using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Budget section's tables: <c>budget_years</c>,
/// <c>budget_groups</c>, <c>budget_categories</c>, <c>budget_line_items</c>,
/// <c>budget_audit_logs</c>, and <c>ticketing_projections</c>. The only
/// non-test file that writes to these DbSets after the Budget migration lands.
/// </summary>
/// <remarks>
/// <c>budget_audit_logs</c> is append-only by convention — only
/// <see cref="AddAuditLogAsync"/> and <see cref="GetAuditLogAsync"/> are
/// exposed. Budget pages are admin-only and low-traffic, so the repository
/// uses the Scoped + <c>HumansDbContext</c> pattern (like
/// <c>ApplicationRepository</c>) rather than the Singleton +
/// <c>IDbContextFactory</c> pattern. Aggregate loads return tracked
/// entities when mutations are expected so the service can commit coherent
/// multi-entity changes through a single <c>SaveChanges</c> on the
/// repository.
/// </remarks>
public interface IBudgetRepository
{
    // ==========================================================================
    // Budget Years
    // ==========================================================================

    /// <summary>
    /// Returns every budget year. Includes groups and categories (read-only,
    /// no line items).
    /// </summary>
    Task<IReadOnlyList<BudgetYear>> GetAllYearsAsync(bool includeArchived, CancellationToken ct = default);

    /// <summary>
    /// Returns the full year graph (groups → categories → line items, plus
    /// ticketing projection and team navigation) for read-only display.
    /// </summary>
    Task<BudgetYear?> GetYearByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns the currently-active budget year's graph, or null if none.
    /// </summary>
    Task<BudgetYear?> GetActiveYearAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a single budget year (tracked, without navigation), used by
    /// mutating operations.
    /// </summary>
    Task<BudgetYear?> FindYearForMutationAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns every budget year currently in <see cref="BudgetYearStatus.Active"/>
    /// status whose id does not match <paramref name="excludingId"/>. Tracked.
    /// </summary>
    Task<IReadOnlyList<BudgetYear>> FindActiveYearsExcludingAsync(
        Guid excludingId, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the given year exists and is in
    /// <see cref="BudgetYearStatus.Closed"/> status.
    /// </summary>
    Task<bool> IsYearClosedAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Persist a new budget year. Does NOT call <c>SaveChanges</c> — the
    /// service is expected to call <see cref="SaveChangesAsync"/> after
    /// staging all related entities (groups, categories, audit log).
    /// </summary>
    void AddYear(BudgetYear year);

    // ==========================================================================
    // Budget Groups
    // ==========================================================================

    /// <summary>
    /// Returns a tracked budget group (no navigation) for updates.
    /// </summary>
    Task<BudgetGroup?> FindGroupForMutationAsync(Guid groupId, CancellationToken ct = default);

    /// <summary>
    /// Returns the department group for a year (tracked, with categories).
    /// </summary>
    Task<BudgetGroup?> GetDepartmentGroupForMutationAsync(Guid budgetYearId, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the year already has a ticketing group.
    /// </summary>
    Task<bool> HasTicketingGroupAsync(Guid budgetYearId, CancellationToken ct = default);

    /// <summary>
    /// Returns the max <see cref="BudgetGroup.SortOrder"/> for a year, or
    /// <c>-1</c> when no groups exist.
    /// </summary>
    Task<int> GetMaxGroupSortOrderAsync(Guid budgetYearId, CancellationToken ct = default);

    /// <summary>
    /// Stage a new budget group in the change tracker.
    /// </summary>
    void AddGroup(BudgetGroup group);

    /// <summary>
    /// Remove a budget group from the change tracker.
    /// </summary>
    void RemoveGroup(BudgetGroup group);

    // ==========================================================================
    // Budget Categories
    // ==========================================================================

    /// <summary>
    /// Returns a single category with its full detail graph (group, year,
    /// team, line items + responsible team) for read-only display.
    /// </summary>
    Task<BudgetCategory?> GetCategoryByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns a tracked category (with group → year for year-closed checks)
    /// for updates.
    /// </summary>
    Task<BudgetCategory?> FindCategoryForMutationAsync(Guid categoryId, CancellationToken ct = default);

    /// <summary>
    /// Returns the max <see cref="BudgetCategory.SortOrder"/> for a group,
    /// or <c>-1</c> when no categories exist.
    /// </summary>
    Task<int> GetMaxCategorySortOrderAsync(Guid budgetGroupId, CancellationToken ct = default);

    /// <summary>
    /// Stage a new category in the change tracker.
    /// </summary>
    void AddCategory(BudgetCategory category);

    /// <summary>
    /// Remove a category from the change tracker.
    /// </summary>
    void RemoveCategory(BudgetCategory category);

    // ==========================================================================
    // Budget Line Items
    // ==========================================================================

    /// <summary>
    /// Returns a single line item (tracked or detached — callers should not
    /// mutate; use <see cref="FindLineItemForMutationAsync"/> for updates).
    /// </summary>
    Task<BudgetLineItem?> GetLineItemByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns a tracked line item with category → group → year so the
    /// service can perform year-closed checks.
    /// </summary>
    Task<BudgetLineItem?> FindLineItemForMutationAsync(Guid lineItemId, CancellationToken ct = default);

    /// <summary>
    /// Returns the max <see cref="BudgetLineItem.SortOrder"/> for a
    /// category, or <c>-1</c> when no items exist.
    /// </summary>
    Task<int> GetMaxLineItemSortOrderAsync(Guid budgetCategoryId, CancellationToken ct = default);

    /// <summary>
    /// Stage a new line item in the change tracker.
    /// </summary>
    void AddLineItem(BudgetLineItem lineItem);

    /// <summary>
    /// Remove a line item from the change tracker.
    /// </summary>
    void RemoveLineItem(BudgetLineItem lineItem);

    // ==========================================================================
    // Ticketing Projection
    // ==========================================================================

    /// <summary>
    /// Stage a new ticketing projection in the change tracker.
    /// </summary>
    void AddTicketingProjection(TicketingProjection projection);

    /// <summary>
    /// Returns the ticketing projection for a budget group (read-only).
    /// </summary>
    Task<TicketingProjection?> GetTicketingProjectionAsync(Guid budgetGroupId, CancellationToken ct = default);

    /// <summary>
    /// Returns a tracked ticketing projection with its owning group so the
    /// service can validate and mutate atomically.
    /// </summary>
    Task<TicketingProjection?> FindTicketingProjectionForMutationAsync(
        Guid budgetGroupId, CancellationToken ct = default);

    /// <summary>
    /// Returns the ticketing group (tracked, with categories → line items
    /// and projection) for a budget year. Used by the ticketing sync /
    /// projection-materialization flow.
    /// </summary>
    Task<BudgetGroup?> GetTicketingGroupForMutationAsync(Guid budgetYearId, CancellationToken ct = default);

    // ==========================================================================
    // Audit Log (append-only)
    // ==========================================================================

    /// <summary>
    /// Stage a new budget audit log entry in the change tracker. Append-only —
    /// there is intentionally no <c>UpdateAuditLog</c> or <c>RemoveAuditLog</c>.
    /// </summary>
    void AddAuditLog(BudgetAuditLog entry);

    /// <summary>
    /// Returns the most recent 500 audit log entries, optionally filtered by
    /// budget year. Includes the actor user for display (cross-domain nav —
    /// will be replaced by service-layer stitching during nav-strip phase).
    /// </summary>
    Task<IReadOnlyList<BudgetAuditLog>> GetAuditLogAsync(
        Guid? budgetYearId, CancellationToken ct = default);

    /// <summary>
    /// Returns every budget audit log entry authored by the given user for
    /// GDPR export. Read-only.
    /// </summary>
    Task<IReadOnlyList<BudgetAuditLog>> GetAuditLogEntriesForUserAsync(
        Guid userId, CancellationToken ct = default);

    // ==========================================================================
    // Unit-of-work
    // ==========================================================================

    /// <summary>
    /// Commit all staged changes. The service calls this exactly once per
    /// public operation so multi-entity mutations (e.g., create year +
    /// default groups + audit log) commit atomically.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns true if there are pending changes in the change tracker.
    /// Used by the ticketing sync path to skip redundant <c>SaveChanges</c>
    /// calls when no actuals were produced.
    /// </summary>
    bool HasPendingChanges();
}
