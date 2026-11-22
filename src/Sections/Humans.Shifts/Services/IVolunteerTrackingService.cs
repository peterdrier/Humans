using Humans.Base.Interfaces;

namespace Humans.Shifts.Services;

internal interface IVolunteerTrackingService : IApplicationService
{
    /// <summary>Null when there is no active event.</summary>
    Task<VolunteerBuildStripDto?> GetUserBuildStripAsync(Guid userId, CancellationToken ct = default);
}
