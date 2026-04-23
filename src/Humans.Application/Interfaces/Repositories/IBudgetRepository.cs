using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Budget section's tables: <c>budget_years</c>,
/// <c>budget_groups</c>, <c>budget_categories</c>, <c>budget_line_items</c>,
/// <c>budget_audit_logs</c>, and <c>ticketing_projections</c>. The only
/// non-test file that writes to these DbSets.
/// </summary>
/// <remarks>
/// Follows the §15b Singleton + <c>IDbContextFactory</c> pattern: every method
/// opens its own short-lived <c>DbContext</c>, performs its work, and saves
/// atomically within that context's lifetime. There is no cross-method unit of
/// work — callers can no longer stage writes through the repository and then
/// flush with a shared <c>SaveChanges</c>. Multi-entity operations that must
/// be atomic (e.g., creating a year with its default groups and categories)
/// are exposed as single high-level repository methods that do the whole
/// materialization inside one <c>DbContext</c>.
/// <para>
/// <c>budget_audit_logs</c> is append-only per §12 — only
/// <see cref="AddAuditLogAsync"/>, <see cref="GetAuditLogAsync"/>, and
/// <see cref="GetAuditLogEntriesForUserAsync"/> are exposed. Audit entries are
/// written via dedicated repository methods alongside the business mutation so
/// both commit inside the same <c>SaveChanges</c>.
/// </para>
/// </remarks>
public interface IBudgetRepository
{
    // ==========================================================================
    // Budget Years — reads
    // ==========================================================================

    /// <summary>
    /// Returns every budget year. Includes groups and categories (read-only,
    /// no line items).
    /// </summary>
    Task<IReadOnlyList<BudgetYear>> GetAllYearsAsync(bool includeArchived, CancellationToken ct = default);

    /// <summary>
    /// Returns the full year graph (groups → categories → line items, plus
    /// ticketing projection) for read-only display. Detached entities.
    /// </summary>
    Task<BudgetYear?> GetYearByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns the currently-active budget year's graph, or null if none.
    /// </summary>
    Task<BudgetYear?> GetActiveYearAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns true if the given year exists and is in
    /// <see cref="BudgetYearStatus.Closed"/> status.
    /// </summary>
    Task<bool> IsYearClosedAsync(Guid id, CancellationToken ct = default);

    // ==========================================================================
    // Budget Years — atomic mutations
    // ==========================================================================

    /// <summary>
    /// Creates a new budget year and seeds it with the standard scaffolding:
    /// a "Departments" group plus one category per budgetable team, a
    /// "Ticketing" group with its projection record plus "Ticket Revenue" /
    /// "Processing Fees" categories, and the matching audit log entry. All
    /// writes commit inside a single <c>DbContext</c>/<c>SaveChanges</c> so
    /// the year cannot exist without its scaffold.
    /// </summary>
    Task CreateYearWithScaffoldAsync(BudgetYearDraft draft, CancellationToken ct = default);

    /// <summary>
    /// Updates the free-text fields on a budget year (<c>Year</c>, <c>Name</c>)
    /// and appends field-level audit entries for any that changed. Atomic.
    /// Returns <c>true</c> if the year existed, <c>false</c> otherwise.
    /// </summary>
    Task<bool> UpdateYearAsync(
        Guid yearId,
        string year,
        string name,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Transitions a budget year to a new status. If the new status is
    /// <see cref="BudgetYearStatus.Active"/>, every other currently-active
    /// year is closed in the same transaction. Writes a field-level audit
    /// entry for the target year's status change plus one for each
    /// auto-closed year. Atomic. Returns <c>true</c> if the year existed.
    /// </summary>
    Task<bool> UpdateYearStatusAsync(
        Guid yearId,
        BudgetYearStatus status,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes a budget year (marks <c>IsDeleted</c>, sets
    /// <c>DeletedAt</c>, moves status to Closed) and writes the archive
    /// audit entry. Throws <see cref="InvalidOperationException"/> if the
    /// year is currently Active — callers must close it first. Returns
    /// <c>true</c> if the year existed.
    /// </summary>
    Task<bool> SoftDeleteYearAsync(
        Guid yearId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Reverses a soft-delete: clears <c>IsDeleted</c>/<c>DeletedAt</c>,
    /// moves status back to Draft, and writes the restore audit entry. No-op
    /// (returns <c>false</c>) if the year is not soft-deleted. Returns
    /// <c>true</c> if a restore actually occurred.
    /// </summary>
    Task<bool> RestoreYearAsync(
        Guid yearId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Ensures every team in <paramref name="budgetableTeams"/> has a
    /// department category in the year's Departments group, appending one
    /// category per missing team and writing an audit entry for each. Throws
    /// if the year is closed or lacks a Departments group. Returns the
    /// number of categories created.
    /// </summary>
    Task<int> SyncDepartmentCategoriesAsync(
        Guid budgetYearId,
        IReadOnlyList<BudgetableTeamRef> budgetableTeams,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Adds the Ticketing group (with its projection row and two default
    /// categories) to the year if it does not already have one. Throws if
    /// the year is closed. Returns <c>true</c> if the group was created,
    /// <c>false</c> if it already existed.
    /// </summary>
    Task<bool> EnsureTicketingGroupAsync(
        Guid budgetYearId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    // ==========================================================================
    // Budget Groups — atomic mutations
    // ==========================================================================

    /// <summary>
    /// Creates a non-scaffold budget group, writes the create audit entry,
    /// and returns the persisted group. Throws if the year is closed or does
    /// not exist.
    /// </summary>
    Task<BudgetGroup> CreateGroupAsync(
        Guid budgetYearId,
        string name,
        bool isRestricted,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Updates a budget group's name, sort order, and restricted flag,
    /// appending field-level audit entries for each changed field. Throws if
    /// the year is closed. Returns <c>true</c> if the group existed.
    /// </summary>
    Task<bool> UpdateGroupAsync(
        Guid groupId,
        string name,
        int sortOrder,
        bool isRestricted,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a budget group and writes the delete audit entry. Refuses to
    /// delete the auto-generated Departments group. Throws if the year is
    /// closed. Returns <c>true</c> if the group existed.
    /// </summary>
    Task<bool> DeleteGroupAsync(
        Guid groupId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    // ==========================================================================
    // Budget Categories — reads
    // ==========================================================================

    /// <summary>
    /// Returns a single category with its full detail graph (group, year,
    /// line items) for read-only display. Detached.
    /// </summary>
    Task<BudgetCategory?> GetCategoryByIdAsync(Guid id, CancellationToken ct = default);

    // ==========================================================================
    // Budget Categories — atomic mutations
    // ==========================================================================

    /// <summary>
    /// Creates a category in a budget group, writes the create audit entry,
    /// and returns the persisted category. Throws if the year is closed or
    /// the group does not exist.
    /// </summary>
    Task<BudgetCategory> CreateCategoryAsync(
        Guid budgetGroupId,
        string name,
        decimal allocatedAmount,
        ExpenditureType expenditureType,
        Guid? teamId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Updates a category's name, allocation, and expenditure type with
    /// field-level audit entries. Throws if the year is closed. Returns
    /// <c>true</c> if the category existed.
    /// </summary>
    Task<bool> UpdateCategoryAsync(
        Guid categoryId,
        string name,
        decimal allocatedAmount,
        ExpenditureType expenditureType,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a category (cascades to its line items) and writes the delete
    /// audit entry. Throws if the year is closed. Returns <c>true</c> if the
    /// category existed.
    /// </summary>
    Task<bool> DeleteCategoryAsync(
        Guid categoryId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    // ==========================================================================
    // Budget Line Items — reads
    // ==========================================================================

    /// <summary>
    /// Returns a single line item (detached), or null.
    /// </summary>
    Task<BudgetLineItem?> GetLineItemByIdAsync(Guid id, CancellationToken ct = default);

    // ==========================================================================
    // Budget Line Items — atomic mutations
    // ==========================================================================

    /// <summary>
    /// Creates a line item under a category, writes the create audit entry,
    /// and returns the persisted line item. Throws if the category's year is
    /// closed or the category does not exist.
    /// </summary>
    Task<BudgetLineItem> CreateLineItemAsync(
        BudgetLineItemDraft draft,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Updates a line item's editable fields with field-level audit entries
    /// for every change. Throws if the year is closed. Returns <c>true</c>
    /// if the line item existed.
    /// </summary>
    Task<bool> UpdateLineItemAsync(
        BudgetLineItemUpdate update,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a line item and writes the delete audit entry. Throws if the
    /// year is closed. Returns <c>true</c> if the line item existed.
    /// </summary>
    Task<bool> DeleteLineItemAsync(
        Guid lineItemId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    // ==========================================================================
    // Ticketing Projection — reads
    // ==========================================================================

    /// <summary>
    /// Returns the ticketing projection for a budget group (read-only).
    /// </summary>
    Task<TicketingProjection?> GetTicketingProjectionAsync(Guid budgetGroupId, CancellationToken ct = default);

    /// <summary>
    /// Returns a budget group (detached, no navs). Used by callers that need
    /// just flags (e.g., <c>IsTicketingGroup</c>) without the full graph.
    /// </summary>
    Task<BudgetGroup?> GetGroupByIdAsync(Guid groupId, CancellationToken ct = default);

    // ==========================================================================
    // Ticketing Projection — atomic mutations
    // ==========================================================================

    /// <summary>
    /// Updates every projection parameter on a ticketing group's projection
    /// row and writes one audit entry. Throws if the target group is not a
    /// ticketing group, if it has no projection row, or if its year is
    /// closed. Returns <c>true</c> if the update succeeded.
    /// </summary>
    Task<bool> UpdateTicketingProjectionAsync(
        TicketingProjectionUpdate update,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Runs the ticketing actuals sync for a budget year in a single
    /// transaction: upserts auto-generated line items for each completed
    /// week (revenue + fees), refreshes the projection's learned parameters
    /// (avg ticket price, fee percentages) from those actuals, then removes
    /// stale projected line items and re-materializes projected line items
    /// for future weeks using the <em>freshly updated</em> projection. All
    /// writes commit inside a single <c>DbContext</c>/<c>SaveChanges</c>.
    /// Returns the number of line items created or updated.
    /// </summary>
    /// <remarks>
    /// The repository owns the projected-week schedule computation so it
    /// runs against the post-update projection parameters. <paramref name="today"/>
    /// defines the current ISO-week Monday cut-over (passed in so the
    /// repository stays <c>IClock</c>-free).
    /// </remarks>
    Task<int> SyncTicketingActualsAsync(
        Guid budgetYearId,
        IReadOnlyList<TicketingWeeklyActualsInput> weeklyActuals,
        LocalDate today,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Re-materializes projected ticketing line items for a year (no actuals
    /// sync). Removes stale projected line items and writes new ones from
    /// the current projection parameters in a single transaction. Returns
    /// the number of projected line items created.
    /// </summary>
    /// <remarks>
    /// <paramref name="today"/> defines the current ISO-week Monday cut-over;
    /// passed in so the repository stays <c>IClock</c>-free.
    /// </remarks>
    Task<int> RefreshTicketingProjectionsAsync(
        Guid budgetYearId,
        LocalDate today,
        Instant now,
        CancellationToken ct = default);

    // ==========================================================================
    // Audit Log — reads (append-only; no update/delete per §12)
    // ==========================================================================

    /// <summary>
    /// Returns the most recent 500 audit log entries, optionally filtered by
    /// budget year.
    /// </summary>
    Task<IReadOnlyList<BudgetAuditLog>> GetAuditLogAsync(
        Guid? budgetYearId, CancellationToken ct = default);

    /// <summary>
    /// Returns every budget audit log entry authored by the given user for
    /// GDPR export. Read-only.
    /// </summary>
    Task<IReadOnlyList<BudgetAuditLog>> GetAuditLogEntriesForUserAsync(
        Guid userId, CancellationToken ct = default);
}

// ==========================================================================
// Input DTOs for atomic mutations
// ==========================================================================

/// <summary>
/// Input for <see cref="IBudgetRepository.CreateYearWithScaffoldAsync"/>.
/// Carries the new year's identity/metadata and the list of budgetable
/// teams whose department categories should be auto-created.
/// </summary>
public sealed record BudgetYearDraft(
    Guid Id,
    string Year,
    string Name,
    IReadOnlyList<BudgetableTeamRef> BudgetableTeams,
    Guid ActorUserId,
    Instant Now);

/// <summary>
/// Minimal team reference for budget scaffolding. The Budget section reads
/// team identities through <see cref="Humans.Application.Interfaces.Teams.ITeamService"/>
/// — this record is the in-memory snapshot passed into repository-side mutations
/// so the repository never has to cross the Teams section's ownership boundary.
/// </summary>
public sealed record BudgetableTeamRef(Guid Id, string Name);

/// <summary>
/// Input for <see cref="IBudgetRepository.CreateLineItemAsync"/>.
/// </summary>
public sealed record BudgetLineItemDraft(
    Guid BudgetCategoryId,
    string Description,
    decimal Amount,
    Guid? ResponsibleTeamId,
    string? Notes,
    LocalDate? ExpectedDate,
    int VatRate);

/// <summary>
/// Input for <see cref="IBudgetRepository.UpdateLineItemAsync"/>.
/// </summary>
public sealed record BudgetLineItemUpdate(
    Guid LineItemId,
    string Description,
    decimal Amount,
    Guid? ResponsibleTeamId,
    string? Notes,
    LocalDate? ExpectedDate,
    int VatRate);

/// <summary>
/// Input for <see cref="IBudgetRepository.UpdateTicketingProjectionAsync"/>.
/// </summary>
public sealed record TicketingProjectionUpdate(
    Guid BudgetGroupId,
    LocalDate? StartDate,
    LocalDate? EventDate,
    int InitialSalesCount,
    decimal DailySalesRate,
    decimal AverageTicketPrice,
    int VatRate,
    decimal StripeFeePercent,
    decimal StripeFeeFixed,
    decimal TicketTailorFeePercent);

/// <summary>
/// Input row for <see cref="IBudgetRepository.SyncTicketingActualsAsync"/>.
/// One completed ISO week of aggregated actuals from the Tickets side.
/// </summary>
public sealed record TicketingWeeklyActualsInput(
    string WeekLabel,
    LocalDate Monday,
    int TicketCount,
    decimal Revenue,
    decimal StripeFees,
    decimal TicketTailorFees);
