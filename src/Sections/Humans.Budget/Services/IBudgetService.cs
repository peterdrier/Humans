using Humans.Application.Interfaces;
using Humans.Budget.Contracts;
using NodaTime;

namespace Humans.Budget.Services;

/// <summary>
/// Budget's full service surface: everything on top of the cross-section
/// <see cref="IBudgetServiceRead"/> that only the section's own controllers, views and
/// bridge service call. Internal on purpose — the assembly boundary is what keeps
/// year/group/category/line-item mutation out of other sections' reach.
/// </summary>
/// <remarks>
/// Kept as an interface rather than injecting the concrete <c>BudgetService</c> because
/// the ticketing bridge's unit tests substitute it (design §15 step 5, "keep an interface
/// only where something needs the seam").
/// </remarks>
internal interface IBudgetService : IBudgetServiceRead, IApplicationService
{
    // Budget Years
    Task<IReadOnlyList<BudgetYearSummarySnapshot>> GetAllYearsAsync(bool includeArchived = false);
    Task<BudgetYearDetail?> GetYearByIdAsync(Guid id);
    Task<CoordinatorBudgetViewData> GetCoordinatorBudgetViewDataAsync(Guid userId, bool isFinanceAdmin);
    Task<BudgetYearDetail> CreateYearAsync(string year, string name, Guid actorUserId);
    Task UpdateYearStatusAsync(Guid yearId, BudgetYearStatus status, Guid actorUserId);
    Task UpdateYearAsync(Guid yearId, string year, string name, Guid actorUserId);
    Task DeleteYearAsync(Guid yearId, Guid actorUserId);
    Task RestoreYearAsync(Guid yearId, Guid actorUserId);

    Task<int> SyncDepartmentsAsync(Guid budgetYearId, Guid actorUserId);
    Task<EnsureTicketingGroupResult> EnsureTicketingGroupAsync(Guid budgetYearId, Guid actorUserId);

    // Ticketing Projection
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
    int GetActualTicketsSold(BudgetGroupDetail ticketingGroup);

    // Budget Groups
    Task<BudgetGroupDetail> CreateGroupAsync(Guid budgetYearId, string name, bool isRestricted, Guid actorUserId);
    Task UpdateGroupAsync(Guid groupId, string name, int sortOrder, bool isRestricted, Guid actorUserId);
    Task DeleteGroupAsync(Guid groupId, Guid actorUserId);

    // Budget Categories
    Task<CoordinatorCategoryDetailViewData> GetCoordinatorCategoryDetailViewDataAsync(Guid categoryId, Guid userId, bool isFinanceAdmin);
    Task<BudgetCategoryDetail> CreateCategoryAsync(Guid budgetGroupId, string name, decimal allocatedAmount, ExpenditureType expenditureType, Guid? teamId, Guid actorUserId);
    Task UpdateCategoryAsync(Guid categoryId, string name, decimal allocatedAmount, ExpenditureType expenditureType, Guid actorUserId);
    Task DeleteCategoryAsync(Guid categoryId, Guid actorUserId);

    // Budget Line Items
    Task<BudgetLineItemSnapshot?> GetLineItemByIdAsync(Guid id);
    Task<BudgetLineItemSnapshot> CreateLineItemAsync(Guid budgetCategoryId, string description, decimal amount, Guid? responsibleTeamId, string? notes, LocalDate? expectedDate, int vatRate, Guid actorUserId);
    Task UpdateLineItemAsync(Guid lineItemId, string description, decimal amount, Guid? responsibleTeamId, string? notes, LocalDate? expectedDate, int vatRate, Guid actorUserId);
    Task DeleteLineItemAsync(Guid lineItemId, Guid actorUserId);

    // Audit Log
    Task<IReadOnlyList<BudgetAuditLogSnapshot>> GetAuditLogAsync(Guid? budgetYearId);

    // Summary Computation
    BudgetSummaryResult ComputeBudgetSummaryWithBuffers(IReadOnlyList<BudgetGroupDetail> groups);
    IReadOnlyList<VatCashFlowEntry> ComputeVatCashFlowEntries(IReadOnlyList<BudgetGroupDetail> groups);
}
