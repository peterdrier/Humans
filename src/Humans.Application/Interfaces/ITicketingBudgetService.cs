using System.Security.Claims;
using Humans.Application.DTOs;
using Humans.Domain.Entities;

namespace Humans.Application.Interfaces;

/// <summary>
/// Materializes ticket sales actuals into budget line items and computes projections for future weeks.
/// </summary>
public interface ITicketingBudgetService
{
    /// <summary>
    /// Sync completed weeks of ticket sales into budget line items from TicketTailor/Stripe data,
    /// then refresh projections for future weeks.
    /// The <paramref name="principal"/> is forwarded to <see cref="IBudgetService"/> for
    /// service-boundary authorization. Background jobs pass
    /// <see cref="Humans.Application.Authorization.SystemPrincipal.Instance"/>.
    /// </summary>
    Task<int> SyncActualsAsync(Guid budgetYearId, ClaimsPrincipal principal, CancellationToken ct = default);

    /// <summary>
    /// Refresh projected line items only (no actuals sync). Called after saving projection parameters.
    /// The <paramref name="principal"/> is forwarded to <see cref="IBudgetService"/> for
    /// service-boundary authorization.
    /// </summary>
    Task<int> RefreshProjectionsAsync(Guid budgetYearId, ClaimsPrincipal principal, CancellationToken ct = default);

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
