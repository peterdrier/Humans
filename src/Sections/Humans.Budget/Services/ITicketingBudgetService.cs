using Humans.Base.Interfaces;

namespace Humans.Budget.Services;

/// <summary>
/// Seam for <c>TicketingBudgetSyncJob</c> to call <c>TicketingBudgetService</c> in tests.
/// Internal (ruling 43) — the interface's only consumer, the sync job, is section-internal too.
/// </summary>
/// <remarks>
/// Projection refresh, the projection-parameter write and the virtual weekly forecast are
/// driven only from Budget's own admin pages, so they live on the concrete
/// <c>TicketingBudgetService</c> rather than here — <c>BudgetAdminController</c>
/// deliberately injects the concrete class.
/// </remarks>
internal interface ITicketingBudgetService : IOrchestrator
{
    /// <summary>
    /// Sync completed weeks of ticket sales into budget line items from TicketTailor/Stripe data,
    /// then refresh projections for future weeks. Returns the number of line items touched.
    /// </summary>
    Task<int> SyncActualsAsync(Guid budgetYearId, CancellationToken ct = default);
}
