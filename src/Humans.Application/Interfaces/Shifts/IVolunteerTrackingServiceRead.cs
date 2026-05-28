using Humans.Application.Architecture;
using Humans.Application.DTOs;

namespace Humans.Application.Interfaces.Shifts;

/// <summary>
/// Cross-section read surface for Volunteer Tracking. External sections inject
/// this (not the full <see cref="IVolunteerTrackingService"/>); returns only
/// the <see cref="VolunteerBuildStripDto"/> projection. See
/// <c>memory/architecture/section-read-write-split.md</c>.
/// </summary>
[SurfaceBudget(1)]
public interface IVolunteerTrackingServiceRead
{
    /// <summary>One volunteer's build-window strip, or null when there is no
    /// active event.</summary>
    Task<VolunteerBuildStripDto?> GetUserBuildStripAsync(Guid userId, CancellationToken ct = default);
}
