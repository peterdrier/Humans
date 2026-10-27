using Humans.Budget.Data;
using Humans.Base.Interfaces;
using Humans.Budget.Contracts;
using NodaTime;

namespace Humans.Budget.Services;

/// <summary>An interface rather than the concrete <c>BudgetService</c> because the ticketing bridge's tests substitute it.</summary>
internal interface IBudgetManagement : IBudgetService, IApplicationService
{
    Task<IReadOnlyList<BudgetYearSummarySnapshot>> GetAllYearsAsync(bool includeArchived = false, CancellationToken ct = default);
    Task<CoordinatorBudgetViewData> GetCoordinatorBudgetViewDataAsync(Guid userId, bool isFinanceAdmin, CancellationToken ct = default);
    Task<BudgetYearDetail> CreateYearAsync(string year, string name, Guid actorUserId);
    Task<BudgetYearMutationResult> UpdateYearStatusAsync(Guid yearId, BudgetYearStatus status, Guid actorUserId);
    Task<BudgetYearMutationResult> UpdateYearAsync(Guid yearId, string year, string name, string? vendorEventId, Guid actorUserId);
    Task<BudgetYearMutationResult> DeleteYearAsync(Guid yearId, Guid actorUserId);

    Task<int> SyncDepartmentsAsync(Guid budgetYearId, Guid actorUserId);
    Task<EnsureTicketingGroupResult> EnsureTicketingGroupAsync(Guid budgetYearId, Guid actorUserId);

    Task UpdateTicketingProjectionAsync(Guid budgetGroupId, LocalDate? startDate, LocalDate? eventDate,
        int initialSalesCount, decimal dailySalesRate, decimal averageTicketPrice, int vatRate,
        decimal stripeFeePercent, decimal stripeFeeFixed, decimal ticketTailorFeePercent, Guid actorUserId);

    /// <summary>
    /// Upserts the completed weeks' revenue and fee rows, refreshes the learned projection parameters from them, and
    /// re-materializes projected weeks. Returns line items created or updated. A null <paramref name="actorUserId"/>
    /// is the nightly job, audited as automation.
    /// </summary>
    Task<int> SyncTicketingActualsAsync(
        Guid budgetYearId,
        IReadOnlyList<TicketingWeeklyActuals> weeklyActuals,
        Guid? actorUserId,
        CancellationToken ct = default);

    /// <summary>No actuals sync; for after projection parameters change. Returns projected line items created.</summary>
    Task<int> RefreshTicketingProjectionsAsync(
        Guid budgetYearId, Guid? actorUserId, CancellationToken ct = default);

    /// <summary>Virtual (not persisted) weeks, for the break-even forecasts.</summary>
    Task<IReadOnlyList<TicketingWeekProjection>> GetTicketingProjectionEntriesAsync(
        Guid budgetGroupId, CancellationToken ct = default);

    /// <summary>Through completed weeks, from the revenue line items' notes.</summary>
    int GetActualTicketsSold(BudgetGroupDetail ticketingGroup);

    Task<BudgetGroupDetail> CreateGroupAsync(Guid budgetYearId, string name, bool isRestricted, Guid actorUserId);
    Task UpdateGroupAsync(Guid groupId, string name, int sortOrder, bool isRestricted, Guid actorUserId);
    Task DeleteGroupAsync(Guid groupId, Guid actorUserId);

    Task<CoordinatorCategoryDetailViewData> GetCoordinatorCategoryDetailViewDataAsync(Guid categoryId, Guid userId, bool isFinanceAdmin, CancellationToken ct = default);
    Task<BudgetCategoryDetail> CreateCategoryAsync(Guid budgetGroupId, string name, decimal allocatedAmount, ExpenditureType expenditureType, Guid? teamId, Guid actorUserId);
    Task UpdateCategoryAsync(Guid categoryId, string name, decimal allocatedAmount, ExpenditureType expenditureType, Guid actorUserId);
    Task DeleteCategoryAsync(Guid categoryId, Guid actorUserId);

    Task<BudgetLineItemSnapshot?> GetLineItemByIdAsync(Guid id);
    Task<BudgetLineItemSnapshot> CreateLineItemAsync(Guid budgetCategoryId, string description, decimal amount, Guid? responsibleTeamId, string? notes, LocalDate? expectedDate, int vatRate, Guid actorUserId);
    Task UpdateLineItemAsync(Guid lineItemId, string description, decimal amount, Guid? responsibleTeamId, string? notes, LocalDate? expectedDate, int vatRate, Guid actorUserId);
    Task DeleteLineItemAsync(Guid lineItemId, Guid actorUserId);

    Task<IReadOnlyList<BudgetAuditLogSnapshot>> GetAuditLogAsync(Guid? budgetYearId, CancellationToken ct = default);

    BudgetSummaryResult ComputeBudgetSummaryWithBuffers(IReadOnlyList<BudgetGroupDetail> groups);
}
