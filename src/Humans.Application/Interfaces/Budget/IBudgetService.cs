using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Budget;

/// <summary>
/// Service for managing budget years, groups, categories, and line items.
/// </summary>
public interface IBudgetService
{
    // Budget Years
    Task<IReadOnlyList<BudgetYear>> GetAllYearsAsync(bool includeArchived = false);
    Task<BudgetYear?> GetYearByIdAsync(Guid id);
    Task<BudgetYear?> GetActiveYearAsync();
    Task<BudgetYear> CreateYearAsync(string year, string name, Guid actorUserId);
    Task UpdateYearStatusAsync(Guid yearId, BudgetYearStatus status, Guid actorUserId);
    Task UpdateYearAsync(Guid yearId, string year, string name, Guid actorUserId);
    Task DeleteYearAsync(Guid yearId, Guid actorUserId);
    Task RestoreYearAsync(Guid yearId, Guid actorUserId);

    Task<int> SyncDepartmentsAsync(Guid budgetYearId, Guid actorUserId);
    Task<bool> EnsureTicketingGroupAsync(Guid budgetYearId, Guid actorUserId);

    // Ticketing Projection
    Task<TicketingProjection?> GetTicketingProjectionAsync(Guid budgetGroupId);
    Task UpdateTicketingProjectionAsync(Guid budgetGroupId, LocalDate? startDate, LocalDate? eventDate,
        int initialSalesCount, decimal dailySalesRate, decimal averageTicketPrice, int vatRate,
        decimal stripeFeePercent, decimal stripeFeeFixed, decimal ticketTailorFeePercent, Guid actorUserId);

    /// <summary>
    /// Sync ticket sales actuals (already aggregated per ISO week by the ticket side)
    /// into the ticketing budget group. Upserts auto-generated BudgetLineItems for
    /// each completed week's revenue and processing fees, refreshes projection
    /// parameters (average ticket price, stripe fee %, TicketTailor fee %) from
    /// those actuals, and re-materializes projected line items for future weeks.
    /// Returns the number of line items created or updated.
    /// </summary>
    Task<int> SyncTicketingActualsAsync(
        Guid budgetYearId,
        IReadOnlyList<TicketingWeeklyActuals> weeklyActuals,
        CancellationToken ct = default);

    /// <summary>
    /// Re-materialize projected ticketing line items (no actuals sync). Called
    /// after projection parameters change so the projected lines reflect the new inputs.
    /// Returns the number of projected line items created.
    /// </summary>
    Task<int> RefreshTicketingProjectionsAsync(Guid budgetYearId, CancellationToken ct = default);

    /// <summary>
    /// Compute virtual (non-persisted) weekly ticket projections for future weeks.
    /// Used by finance overview pages to display break-even forecasts.
    /// </summary>
    Task<IReadOnlyList<TicketingWeekProjection>> GetTicketingProjectionEntriesAsync(
        Guid budgetGroupId, CancellationToken ct = default);

    /// <summary>
    /// Compute the total number of tickets sold through completed weeks, derived
    /// from the revenue line item notes on an already-loaded ticketing group.
    /// </summary>
    int GetActualTicketsSold(BudgetGroup ticketingGroup);

    // Budget Groups
    Task<BudgetGroup> CreateGroupAsync(Guid budgetYearId, string name, bool isRestricted, Guid actorUserId);
    Task UpdateGroupAsync(Guid groupId, string name, int sortOrder, bool isRestricted, Guid actorUserId);
    Task DeleteGroupAsync(Guid groupId, Guid actorUserId);

    // Budget Categories
    Task<BudgetCategory?> GetCategoryByIdAsync(Guid id);
    Task<BudgetCategory> CreateCategoryAsync(Guid budgetGroupId, string name, decimal allocatedAmount, ExpenditureType expenditureType, Guid? teamId, Guid actorUserId);
    Task UpdateCategoryAsync(Guid categoryId, string name, decimal allocatedAmount, ExpenditureType expenditureType, Guid actorUserId);
    Task DeleteCategoryAsync(Guid categoryId, Guid actorUserId);

    // Budget Line Items
    Task<BudgetLineItem?> GetLineItemByIdAsync(Guid id);
    Task<BudgetLineItem> CreateLineItemAsync(Guid budgetCategoryId, string description, decimal amount, Guid? responsibleTeamId, string? notes, LocalDate? expectedDate, int vatRate, Guid actorUserId);
    Task UpdateLineItemAsync(Guid lineItemId, string description, decimal amount, Guid? responsibleTeamId, string? notes, LocalDate? expectedDate, int vatRate, Guid actorUserId);
    Task DeleteLineItemAsync(Guid lineItemId, Guid actorUserId);

    // Coordinator
    Task<HashSet<Guid>> GetEffectiveCoordinatorTeamIdsAsync(Guid userId);

    // Audit Log
    Task<IReadOnlyList<BudgetAuditLog>> GetAuditLogAsync(Guid? budgetYearId);

    // Summary Computation
    BudgetSummaryResult ComputeBudgetSummary(IEnumerable<BudgetGroup> groups);
    BudgetSummaryResult ComputeBudgetSummaryWithBuffers(IEnumerable<BudgetGroup> groups);
    IReadOnlyList<VatCashFlowEntry> ComputeVatCashFlowEntries(IEnumerable<BudgetGroup> groups);
    LocalDate ComputeVatSettlementDate(LocalDate expectedDate);
}
