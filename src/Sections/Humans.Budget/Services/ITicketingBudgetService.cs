using Humans.Base.Interfaces;

namespace Humans.Budget.Services;

/// <summary>The sync job's test seam. Projection actions are admin-only, so the admin controller uses the concrete class.</summary>
internal interface ITicketingBudgetService : IOrchestrator
{
    /// <summary>
    /// Syncs completed weeks, then refreshes projections. Returns line items touched. A null <paramref name="actorUserId"/> is
    /// the nightly job, audited as automation.
    /// </summary>
    Task<int> SyncActualsAsync(Guid budgetYearId, Guid? actorUserId, CancellationToken ct = default);
}
