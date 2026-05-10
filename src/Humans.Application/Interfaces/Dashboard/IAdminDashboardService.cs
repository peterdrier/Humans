using Humans.Application.Interfaces;
using Humans.Application.DTOs;

namespace Humans.Application.Interfaces.Dashboard;

/// <summary>
/// Aggregation surface for the Board / Admin dashboard. Lives next to
/// <see cref="IDashboardService"/> (which serves the per-member dashboard)
/// but is its own contract — the shapes and consumers are different
/// (single-user view vs. global aggregates) so they don't share a budget
/// or a service implementation.
/// </summary>
public interface IAdminDashboardService : IApplicationService
{
    /// <summary>
    /// Aggregates member counts (by membership partition), tier-application
    /// stats, and language distribution into the admin dashboard snapshot.
    /// </summary>
    Task<AdminDashboardData> GetAdminDashboardAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the count of profiles pending consent review (badge counter
    /// for the admin nav). Currently a thin pass-through to the Profile
    /// section's pending-review query — colocated here because the consumer
    /// is the admin nav badge, not an onboarding orchestration entry point.
    /// </summary>
    Task<int> GetPendingReviewCountAsync(CancellationToken ct = default);
}
