using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces;

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

    Task<int> SyncDepartmentsAsync(Guid budgetYearId, Guid actorUserId);
    Task<bool> EnsureTicketingGroupAsync(Guid budgetYearId, Guid actorUserId);

    // Ticketing Projection
    Task<TicketingProjection?> GetTicketingProjectionAsync(Guid budgetGroupId);
    Task UpdateTicketingProjectionAsync(Guid budgetGroupId, LocalDate? startDate, LocalDate? eventDate,
        int initialSalesCount, decimal dailySalesRate, decimal averageTicketPrice, int vatRate,
        decimal stripeFeePercent, decimal stripeFeeFixed, decimal ticketTailorFeePercent, Guid actorUserId);

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
    Task<BudgetLineItem> CreateLineItemAsync(Guid budgetCategoryId, string description, decimal amount, Guid? responsibleTeamId, string? notes, LocalDate? expectedDate, int vatRate, Guid actorUserId);
    Task UpdateLineItemAsync(Guid lineItemId, string description, decimal amount, Guid? responsibleTeamId, string? notes, LocalDate? expectedDate, int vatRate, Guid actorUserId);
    Task DeleteLineItemAsync(Guid lineItemId, Guid actorUserId);

    // Coordinator
    Task<HashSet<Guid>> GetEffectiveCoordinatorTeamIdsAsync(Guid userId);

    // Audit Log
    Task<IReadOnlyList<BudgetAuditLog>> GetAuditLogAsync(Guid? budgetYearId);

    // Summary Computation
    BudgetSummaryResult ComputeBudgetSummary(IEnumerable<BudgetGroup> groups);
    IReadOnlyList<VatCashFlowEntry> ComputeVatCashFlowEntries(IEnumerable<BudgetGroup> groups);
    LocalDate ComputeVatSettlementDate(LocalDate expectedDate);
}
