using Humans.Budget.Contracts;
using NodaTime;

namespace Humans.Budget.Services;

internal sealed record BudgetYearSummarySnapshot(
    Guid Id,
    string Year,
    string Name,
    BudgetYearStatus Status,
    bool IsDeleted,
    IReadOnlyList<BudgetGroupSummarySnapshot> Groups,
    string? VendorEventId = null);

internal sealed record BudgetGroupSummarySnapshot(
    Guid Id,
    string Name,
    int SortOrder,
    bool IsRestricted,
    bool IsDepartmentGroup);

internal sealed record BudgetAuditLogSnapshot(
    Guid Id,
    Guid BudgetYearId,
    string EntityType,
    Guid EntityId,
    string? FieldName,
    string? OldValue,
    string? NewValue,
    string Description,
    Guid? ActorUserId,
    Instant OccurredAt);

internal sealed record EnsureTicketingGroupResult(bool Created, string Message);

internal sealed record TicketingProjectionUpdateCommand(
    Guid BudgetGroupId,
    Guid BudgetYearId,
    LocalDate? StartDate,
    LocalDate? EventDate,
    int InitialSalesCount,
    decimal DailySalesRate,
    decimal AverageTicketPrice,
    int VatRate,
    decimal StripeFeePercent,
    decimal StripeFeeFixed,
    decimal TicketTailorFeePercent);

internal readonly record struct BudgetSaveResult<T>(
    T? Value = null, string? Refusal = null) where T : class;

internal readonly record struct TicketingProjectionSaveResult(int Count = 0, string? Refusal = null);

internal readonly record struct BudgetSaveResult(string? Refusal = null);
