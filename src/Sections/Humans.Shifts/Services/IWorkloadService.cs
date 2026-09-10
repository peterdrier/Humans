using Humans.Shifts.Services.Dtos;
using Humans.Base.Interfaces;

namespace Humans.Shifts.Services;

/// <summary>
/// Workload aggregations across the Shifts domain — "who is doing how much"
/// rolled up per-person, per-shift, and per-department for the active event.
/// Read-only; no own cache — served off the cached IShiftRowView. Coordinator dashboard
/// surface (<c>/Shifts/Admin/Workload</c>, <c>ShiftDashboardAccess</c> policy).
/// </summary>
internal interface IWorkloadService : IApplicationService
{
    /// <summary>
    /// Computes the workload report for the active event. Returns <c>null</c>
    /// when no event is active.
    /// </summary>
    Task<WorkloadReport?> GetForActiveEventAsync(CancellationToken ct = default);
}
