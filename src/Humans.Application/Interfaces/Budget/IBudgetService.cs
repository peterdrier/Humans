using Humans.Application.DTOs;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Budget;

/// <summary>
/// Service for managing budget years, groups, categories, and line items.
/// </summary>
public interface IBudgetService : IApplicationService
{
    // Budget Years
    Task<IReadOnlyList<BudgetYearSummarySnapshot>> GetAllYearsAsync(bool includeArchived = false);
    Task<BudgetYear?> GetYearByIdAsync(Guid id);
    Task<BudgetYear?> GetActiveYearAsync();
    Task<CoordinatorBudgetViewData> GetCoordinatorBudgetViewDataAsync(Guid userId, bool isFinanceAdmin);
    Task<BudgetYear> CreateYearAsync(string year, string name, Guid actorUserId);
    Task UpdateYearStatusAsync(Guid yearId, BudgetYearStatus status, Guid actorUserId);
    Task UpdateYearAsync(Guid yearId, string year, string name, Guid actorUserId);
    Task DeleteYearAsync(Guid yearId, Guid actorUserId);
    Task RestoreYearAsync(Guid yearId, Guid actorUserId);

    Task<int> SyncDepartmentsAsync(Guid budgetYearId, Guid actorUserId);
    Task<EnsureTicketingGroupResult> EnsureTicketingGroupAsync(Guid budgetYearId, Guid actorUserId);

    // Ticketing Projection
    Task<TicketingProjectionSnapshot?> GetTicketingProjectionAsync(Guid budgetGroupId);
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
    Task<BudgetCategorySnapshot?> GetCategoryByIdAsync(Guid id);
    Task<CoordinatorCategoryDetailViewData> GetCoordinatorCategoryDetailViewDataAsync(Guid categoryId, Guid userId, bool isFinanceAdmin);
    Task<BudgetCategory> CreateCategoryAsync(Guid budgetGroupId, string name, decimal allocatedAmount, ExpenditureType expenditureType, Guid? teamId, Guid actorUserId);
    Task UpdateCategoryAsync(Guid categoryId, string name, decimal allocatedAmount, ExpenditureType expenditureType, Guid actorUserId);
    Task DeleteCategoryAsync(Guid categoryId, Guid actorUserId);

    // Budget Line Items
    Task<BudgetLineItemSnapshot?> GetLineItemByIdAsync(Guid id);
    Task<BudgetLineItem> CreateLineItemAsync(Guid budgetCategoryId, string description, decimal amount, Guid? responsibleTeamId, string? notes, LocalDate? expectedDate, int vatRate, Guid actorUserId);
    Task<BudgetMutationResult> CreateLineItemWithResultAsync(Guid budgetCategoryId, string description, decimal amount, Guid? responsibleTeamId, string? notes, LocalDate? expectedDate, int vatRate, Guid actorUserId);
    Task UpdateLineItemAsync(Guid lineItemId, string description, decimal amount, Guid? responsibleTeamId, string? notes, LocalDate? expectedDate, int vatRate, Guid actorUserId);
    Task<BudgetMutationResult> UpdateLineItemWithResultAsync(Guid lineItemId, string description, decimal amount, Guid? responsibleTeamId, string? notes, LocalDate? expectedDate, int vatRate, Guid actorUserId);
    Task DeleteLineItemAsync(Guid lineItemId, Guid actorUserId);

    // Coordinator
    Task<HashSet<Guid>> GetEffectiveCoordinatorTeamIdsAsync(Guid userId);

    // Audit Log
    Task<IReadOnlyList<BudgetAuditLogSnapshot>> GetAuditLogAsync(Guid? budgetYearId);

    // Summary Computation
    BudgetSummaryResult ComputeBudgetSummary(IEnumerable<BudgetGroup> groups);
    BudgetSummaryResult ComputeBudgetSummaryWithBuffers(IEnumerable<BudgetGroup> groups);
    IReadOnlyList<VatCashFlowEntry> ComputeVatCashFlowEntries(IEnumerable<BudgetGroup> groups);
    LocalDate ComputeVatSettlementDate(LocalDate expectedDate);
}

public sealed record TicketingProjectionSnapshot(
    Guid Id,
    Guid BudgetGroupId,
    LocalDate? StartDate,
    LocalDate? EventDate,
    int InitialSalesCount,
    decimal DailySalesRate,
    decimal AverageTicketPrice,
    int VatRate,
    decimal StripeFeePercent,
    decimal StripeFeeFixed,
    decimal TicketTailorFeePercent,
    Instant CreatedAt,
    Instant UpdatedAt);

public sealed record EnsureTicketingGroupResult(bool Created, string Message);

public sealed record BudgetAuditLogSnapshot(
    Guid Id,
    Guid BudgetYearId,
    string EntityType,
    Guid EntityId,
    string? FieldName,
    string? OldValue,
    string? NewValue,
    string Description,
    Guid ActorUserId,
    Instant OccurredAt);

public sealed record BudgetMutationResult(bool Succeeded, string? ErrorMessage)
{
    public static BudgetMutationResult Success { get; } = new(true, null);

    public static BudgetMutationResult Failure(string message) => new(false, message);
}

public sealed record CoordinatorBudgetViewData(
    BudgetYear? Year,
    HashSet<Guid> EditableTeamIds,
    bool IsFinanceAdmin,
    bool ShouldRedirectToSummary);

public sealed record CoordinatorCategoryDetailViewData(
    BudgetCategorySnapshot? Category,
    bool ShouldForbid,
    IReadOnlyList<TeamInfo> Teams);

public sealed record BudgetYearSummarySnapshot(
    Guid Id,
    string Year,
    string Name,
    BudgetYearStatus Status,
    bool IsDeleted,
    IReadOnlyList<BudgetGroupSummarySnapshot> Groups);

public sealed record BudgetGroupSummarySnapshot(
    Guid Id,
    string Name,
    int SortOrder,
    bool IsRestricted,
    bool IsDepartmentGroup);

public sealed record BudgetLineItemSnapshot(
    Guid Id,
    Guid BudgetCategoryId,
    string Description,
    decimal Amount,
    Guid? ResponsibleTeamId,
    string? Notes,
    LocalDate? ExpectedDate,
    int VatRate,
    bool IsAutoGenerated,
    bool IsCashflowOnly,
    int SortOrder);

public sealed record BudgetCategorySnapshot(
    Guid Id,
    Guid BudgetGroupId,
    string Name,
    decimal AllocatedAmount,
    ExpenditureType ExpenditureType,
    Guid? TeamId,
    int SortOrder,
    BudgetCategoryGroupSnapshot? BudgetGroup,
    IReadOnlyList<BudgetCategoryLineItemSnapshot> LineItems);

public sealed record BudgetCategoryGroupSnapshot(
    Guid Id,
    Guid BudgetYearId,
    string Name,
    bool IsRestricted,
    bool IsTicketingGroup,
    BudgetCategoryYearSnapshot? BudgetYear);

public sealed record BudgetCategoryYearSnapshot(
    Guid Id,
    string Year,
    string Name,
    bool IsDeleted);

public sealed record BudgetCategoryLineItemSnapshot(
    Guid Id,
    Guid BudgetCategoryId,
    string Description,
    decimal Amount,
    Guid? ResponsibleTeamId,
    string? ResponsibleTeamName,
    string? Notes,
    LocalDate? ExpectedDate,
    int VatRate,
    bool IsAutoGenerated,
    bool IsCashflowOnly,
    int SortOrder);
