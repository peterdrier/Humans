namespace Humans.Application.Interfaces.CityPlanning;

/// <summary>
/// Cross-section read surface for the City Planning section. External sections
/// inject this interface; it exposes only the per-year settings projection
/// (<see cref="CityPlanningSettingsDto"/>), the registration-info scalar, and the
/// City Planning team-membership check — no EF entities, no writes.
/// See <c>memory/architecture/section-read-write-split.md</c>.
/// </summary>
public interface ICityPlanningServiceRead
{
    /// <summary>
    /// Gets the per-year City Planning settings (creates the row on demand for
    /// the current PublicYear).
    /// </summary>
    Task<CityPlanningSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the registration-info markdown keyed to the highest open season
    /// (falls back to PublicYear).
    /// </summary>
    Task<string?> GetRegistrationInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the user is an active member of the City Planning team.
    /// </summary>
    Task<bool> IsCityPlanningTeamMemberAsync(Guid userId, CancellationToken cancellationToken = default);
}
