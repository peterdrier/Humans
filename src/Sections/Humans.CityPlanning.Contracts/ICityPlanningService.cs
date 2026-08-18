using Humans.Base.Interfaces;

namespace Humans.CityPlanning.Contracts;

/// <summary>
/// The two writes City Planning exposes outside itself, on top of
/// <see cref="ICityPlanningServiceRead"/>. Contracts holds everything consumed from outside
/// the section, read <em>or</em> write, until the project-wide read/write split lands
/// (design §15.5b). Everything else the section's own service does — polygon save/restore,
/// placement phases, zone uploads, export — is internal to <c>Humans.CityPlanning</c>.
/// </summary>
public interface ICityPlanningService : ICityPlanningServiceRead, IApplicationService
{
    /// <summary>
    /// Deletes every polygon and history row belonging to the given camp seasons.
    /// Returns the number of rows removed. Called by the Camps section when a Camp
    /// is deleted, before its seasons go — the <c>Restrict</c> FK that used to make
    /// the database refuse that delete was dropped by
    /// nobodies-collective/Humans#992.
    /// </summary>
    Task<int> DeleteCampPolygonsForSeasonsAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the registration-info markdown for the current settings row. Written from the
    /// Camps admin page, which owns the URL the barrio leads are sent to.
    /// </summary>
    Task UpdateRegistrationInfoAsync(string? registrationInfo, CancellationToken cancellationToken = default);
}
