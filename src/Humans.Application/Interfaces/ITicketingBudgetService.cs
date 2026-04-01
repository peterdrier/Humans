using Humans.Application.DTOs;

namespace Humans.Application.Interfaces;

/// <summary>
/// Materializes ticket sales actuals into budget line items and computes projections for future weeks.
/// </summary>
public interface ITicketingBudgetService
{
    /// <summary>
    /// Sync completed weeks of ticket sales into budget line items.
    /// Creates/updates revenue, fee, VAT, and donation line items per week.
    /// </summary>
    Task<int> SyncActualsAsync(Guid budgetYearId);

    /// <summary>
    /// Compute projected line items for future weeks based on ticketing projection parameters
    /// and the latest actuals. Returns virtual (non-persisted) entries for display.
    /// </summary>
    Task<IReadOnlyList<TicketingWeekProjection>> GetProjectionsAsync(Guid budgetGroupId);
}
