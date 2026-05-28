using Humans.Application.DTOs;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Tickets;

/// <summary>
/// Materializes ticket sales actuals into budget line items and computes projections for future weeks.
/// </summary>
public interface ITicketingBudgetService : IOrchestrator
{
    /// <summary>
    /// Sync completed weeks of ticket sales into budget line items from TicketTailor/Stripe data,
    /// then refresh projections for future weeks.
    /// </summary>
    Task<int> SyncActualsAsync(Guid budgetYearId, CancellationToken ct = default);

    /// <summary>
    /// Refresh projected line items only (no actuals sync). Called after saving projection parameters.
    /// </summary>
    Task<int> RefreshProjectionsAsync(Guid budgetYearId, CancellationToken ct = default);

    /// <summary>
    /// Saves ticketing projection parameters and refreshes projected line items.
    /// </summary>
    Task<int> UpdateProjectionAndRefreshAsync(TicketingProjectionUpdateCommand command, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Compute projected line items for future weeks based on ticketing projection parameters
    /// and the latest actuals. Returns virtual (non-persisted) entries for display.
    /// </summary>
    Task<IReadOnlyList<TicketingWeekProjection>> GetProjectionsAsync(Guid budgetGroupId);

    /// <summary>
    /// Returns the total number of tickets sold through completed weeks, derived from synced
    /// revenue line item notes (e.g. "187 tickets"). Consistent with existing sync logic
    /// that only includes completed ISO weeks.
    /// </summary>
    int GetActualTicketsSold(BudgetGroup ticketingGroup);
}

public sealed record TicketingProjectionUpdateCommand(
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
