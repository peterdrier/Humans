using Humans.Application.DTOs.Shifts.Workload;

namespace Humans.Application.Interfaces.Shifts.Workload;

/// <summary>
/// Workload aggregations across the Shifts domain — "who is doing how much"
/// rolled up per-person, per-shift, and per-department for the active event.
/// Read-only. Service-level cache (§15 Option B); coordinator dashboard
/// surface (<c>/Shifts/Admin/Workload</c>, <c>ShiftDashboardAccess</c> policy).
/// </summary>
/// <remarks>
/// Role-based hours (role hours + shift hours unified) are deferred until
/// <c>TeamRoleDefinition.EstimatedHours</c> ships — see
/// nobodies-collective/Humans#734.
/// </remarks>
public interface IWorkloadService : IApplicationService
{
    /// <summary>
    /// Computes the workload report for the active event. Returns <c>null</c>
    /// when no event is active.
    /// </summary>
    Task<WorkloadReport?> GetForActiveEventAsync(CancellationToken ct = default);
}
