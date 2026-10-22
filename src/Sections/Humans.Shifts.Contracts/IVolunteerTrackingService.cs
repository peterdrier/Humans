using Humans.Base.Interfaces;

namespace Humans.Shifts.Contracts;

public interface IVolunteerTrackingService : IApplicationService
{
    /// <summary>Null when there is no active event.</summary>
    Task<VolunteerBuildStripDto?> GetUserBuildStripAsync(Guid userId, CancellationToken ct = default);
}
