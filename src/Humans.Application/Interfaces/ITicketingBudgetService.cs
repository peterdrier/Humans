using Humans.Application.DTOs;

namespace Humans.Application.Interfaces;

/// <summary>
/// Materializes ticket sales actuals into budget line items and computes projections for future weeks.
/// </summary>
public interface ITicketingBudgetService
{
    /// <summary>
    /// Sync completed weeks of ticket sales into budget line items from TicketTailor/Stripe data,
    /// then refresh projections for future weeks.
    /// </summary>
    Task<int> SyncActualsAsync(Guid budgetYearId);

    /// <summary>
    /// Refresh projected line items only (no actuals sync). Called after saving projection parameters.
    /// </summary>
    Task<int> RefreshProjectionsAsync(Guid budgetYearId);

    /// <summary>
    /// Compute projected line items for future weeks based on ticketing projection parameters
    /// and the latest actuals. Returns virtual (non-persisted) entries for display.
    /// </summary>
    Task<IReadOnlyList<TicketingWeekProjection>> GetProjectionsAsync(Guid budgetGroupId);
}
