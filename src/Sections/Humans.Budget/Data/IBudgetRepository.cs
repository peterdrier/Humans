using Humans.Budget.Domain;
using Humans.Budget.Contracts;
using NodaTime;
using Humans.Base.Interfaces.Repositories;

namespace Humans.Budget.Data;

/// <summary>
/// No cross-method unit of work: each method saves within its own context, so writes that must be atomic (a year with
/// its scaffold) are single methods. Each mutation writes its audit entries in the same save. A null actor on an entry
/// means automation.
/// </summary>
internal interface IBudgetRepository : IRepository
{
    /// <summary>With groups and categories, no line items.</summary>
    Task<IReadOnlyList<BudgetYear>> GetAllYearsAsync(bool includeArchived, CancellationToken ct = default);

    /// <summary>The full graph, detached.</summary>
    Task<BudgetYear?> GetYearByIdAsync(Guid id, CancellationToken ct = default);

    Task<BudgetYear?> GetActiveYearAsync(CancellationToken ct = default);

    /// <summary>
    /// Seeds a "Departments" group with one category per budgetable team and a "Ticketing" group with its projection and
    /// two categories, in one save, so a year never exists without its scaffold.
    /// </summary>
    Task CreateYearWithScaffoldAsync(BudgetYearDraft draft, CancellationToken ct = default);

    /// <summary>Audits each changed field; returns a refusal when the year is missing or Closed.</summary>
    Task<BudgetYearMutationResult> UpdateYearAsync(
        Guid yearId,
        string year,
        string name,
        string? vendorEventId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Activating closes every other Active year in the same save, auditing each. Refuses missing or archived years.
    /// </summary>
    Task<BudgetYearMutationResult> UpdateYearStatusAsync(
        Guid yearId,
        BudgetYearStatus status,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Archives (sets <c>IsDeleted</c> and Closed). Refuses Active or missing years.
    /// </summary>
    Task<BudgetYearMutationResult> SoftDeleteYearAsync(
        Guid yearId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Adds a Departments category for each team missing one. Throws if the year is closed or has no Departments group.
    /// Returns categories created.
    /// </summary>
    Task<int> SyncDepartmentCategoriesAsync(
        Guid budgetYearId,
        IReadOnlyList<BudgetableTeamRef> budgetableTeams,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>Throws if the year is closed. False when the group already existed.</summary>
    Task<bool> EnsureTicketingGroupAsync(
        Guid budgetYearId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>Throws if the year is closed or does not exist.</summary>
    Task<BudgetGroup> CreateGroupAsync(
        Guid budgetYearId,
        string name,
        bool isRestricted,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>Audits each changed field. Throws if the year is closed. False when the group does not exist.</summary>
    Task<bool> UpdateGroupAsync(
        Guid groupId,
        string name,
        int sortOrder,
        bool isRestricted,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Refuses the auto-generated Departments group. Throws if the year is closed. False when the group does not exist.
    /// </summary>
    Task<bool> DeleteGroupAsync(
        Guid groupId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>With group, year and line items, detached.</summary>
    Task<BudgetCategory?> GetCategoryByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Throws if the year is closed or the group does not exist.</summary>
    Task<BudgetCategory> CreateCategoryAsync(
        Guid budgetGroupId,
        string name,
        decimal allocatedAmount,
        ExpenditureType expenditureType,
        Guid? teamId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>Audits each changed field. Throws if the year is closed. False when the category does not exist.</summary>
    Task<bool> UpdateCategoryAsync(
        Guid categoryId,
        string name,
        decimal allocatedAmount,
        ExpenditureType expenditureType,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>Cascades to line items. Throws if the year is closed. False when the category does not exist.</summary>
    Task<bool> DeleteCategoryAsync(
        Guid categoryId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>Detached.</summary>
    Task<BudgetLineItem?> GetLineItemByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Throws if the year is closed or the category does not exist.</summary>
    Task<BudgetLineItem> CreateLineItemAsync(
        BudgetLineItemDraft draft,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>Audits each change. Throws if the year is closed. False when the line item does not exist.</summary>
    Task<bool> UpdateLineItemAsync(
        BudgetLineItemUpdate update,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>Throws if the year is closed. False when the line item does not exist.</summary>
    Task<bool> DeleteLineItemAsync(
        Guid lineItemId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    Task<TicketingProjection?> GetTicketingProjectionAsync(Guid budgetGroupId, CancellationToken ct = default);

    /// <summary>
    /// One audit entry. Throws if the group is not a ticketing group, has no projection row, or its year is closed.
    /// </summary>
    Task<bool> UpdateTicketingProjectionAsync(
        TicketingProjectionUpdate update,
        Guid actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// In one save: upserts each completed week's revenue and fee rows, refreshes the learned projection parameters,
    /// then re-materializes projected weeks from the <em>freshly updated</em> projection. Returns line items created or
    /// updated.
    /// </summary>
    /// <remarks>
    /// <paramref name="today"/> sets the current ISO-week cut-over, passed in so the repository stays
    /// <c>IClock</c>-free. A null <paramref name="actorUserId"/> audits the sync as automation.
    /// </remarks>
    Task<int> SyncTicketingActualsAsync(
        Guid budgetYearId,
        IReadOnlyList<TicketingWeeklyActualsInput> weeklyActuals,
        LocalDate today,
        Guid? actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// No actuals sync, one save. Returns projected line items created. <paramref name="today"/> sets the ISO-week
    /// cut-over, passed in so the repository stays <c>IClock</c>-free.
    /// </summary>
    Task<int> RefreshTicketingProjectionsAsync(
        Guid budgetYearId,
        LocalDate today,
        Guid? actorUserId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>The most recent 500.</summary>
    Task<IReadOnlyList<BudgetAuditLog>> GetAuditLogAsync(
        Guid? budgetYearId, CancellationToken ct = default);

    /// <summary>
    /// For the GDPR export, which includes entries left on merged-away ids. Newest first.
    /// </summary>
    Task<IReadOnlyList<BudgetAuditLog>> GetAuditLogEntriesForUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}

internal sealed record BudgetYearDraft(
    Guid Id,
    string Year,
    string Name,
    IReadOnlyList<BudgetableTeamRef> BudgetableTeams,
    Guid ActorUserId,
    Instant Now);

internal sealed record BudgetableTeamRef(Guid Id, string Name);

internal sealed record BudgetLineItemDraft(
    Guid BudgetCategoryId,
    string Description,
    decimal Amount,
    Guid? ResponsibleTeamId,
    string? Notes,
    LocalDate? ExpectedDate,
    int VatRate);

internal sealed record BudgetLineItemUpdate(
    Guid LineItemId,
    string Description,
    decimal Amount,
    Guid? ResponsibleTeamId,
    string? Notes,
    LocalDate? ExpectedDate,
    int VatRate);

internal sealed record TicketingProjectionUpdate(
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

internal sealed record TicketingWeeklyActualsInput(
    string WeekLabel,
    LocalDate Monday,
    int TicketCount,
    decimal Revenue,
    decimal StripeFees,
    decimal TicketTailorFees,
    decimal StripeFeeTax,
    decimal StripeFeesTaxUnknown,
    decimal StripeFeesUntaxed);

internal sealed record BudgetYearMutationResult(string? Refusal = null)
{
    public bool Succeeded => Refusal is null;
}
