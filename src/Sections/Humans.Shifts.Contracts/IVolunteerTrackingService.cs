namespace Humans.Shifts.Contracts;

/// <summary>
/// Public contract for Volunteer Tracking. External sections inject
/// this (not the full <c>IVolunteerTrackingManagement</c>); returns only
/// the <see cref="VolunteerBuildStripDto"/> projection, which
/// backs <c>&lt;vc:volunteer-build-strip&gt;</c>.
/// </summary>
public interface IVolunteerTrackingService
{
    /// <summary>One volunteer's build-window strip, or null when there is no
    /// active event.</summary>
    Task<VolunteerBuildStripDto?> GetUserBuildStripAsync(Guid userId, CancellationToken ct = default);
}
