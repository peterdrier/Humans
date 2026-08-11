using Humans.Application.Interfaces;

namespace Humans.Budget.Contracts;

/// <summary>
/// The one thing the Tickets→Budget bridge exposes outside the section: materializing
/// completed weeks of ticket sales into budget line items. Driven by
/// <c>TicketingBudgetSyncJob</c>, which stays in <c>Humans.Infrastructure/Jobs</c>
/// because recurring jobs are named by concrete type in Shell's roll-call
/// (design §15.6b).
/// </summary>
/// <remarks>
/// Projection refresh, the projection-parameter write and the virtual weekly forecast are
/// driven only from Budget's own admin pages, so they live on the internal
/// <c>TicketingBudgetService</c> rather than here.
/// </remarks>
public interface ITicketingBudgetService : IOrchestrator
{
    /// <summary>
    /// Sync completed weeks of ticket sales into budget line items from TicketTailor/Stripe data,
    /// then refresh projections for future weeks. Returns the number of line items touched.
    /// </summary>
    Task<int> SyncActualsAsync(Guid budgetYearId, CancellationToken ct = default);
}
