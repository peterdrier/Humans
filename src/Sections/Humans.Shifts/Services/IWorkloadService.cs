using Humans.Shifts.Services.Dtos;
using Humans.Base.Interfaces;

namespace Humans.Shifts.Services;

internal interface IWorkloadService : IApplicationService
{
    /// <summary>Null when no event is active.</summary>
    Task<WorkloadReport?> GetForActiveEventAsync(CancellationToken ct = default);
}
