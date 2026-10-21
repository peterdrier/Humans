using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.CityPlanning.Contracts;

/// <summary>
/// Public contract of the City Planning section. External sections
/// inject this interface; it exposes only the per-year settings projection
/// (<see cref="CityPlanningSettingsDto"/>), the registration-info scalar, and the
/// City Planning team-membership check — no EF entities, no writes.
/// </summary>
public interface ICityPlanningService : IApplicationService
{
    /// <summary>
    /// Gets the per-year City Planning settings for the requested year, or the active
    /// event's year when omitted. Side-effect free: a year with no row yet reads as the
    /// unpersisted defaults (<c>Id</c> = <see cref="Guid.Empty"/>). Null only when the
    /// year is omitted and no event is active.
    /// </summary>
    Task<CityPlanningSettingsDto?> GetSettingsAsync(CancellationToken cancellationToken = default, int? year = null);

    /// <summary>
    /// Gets the registration-info markdown keyed to the highest open season
    /// (falls back to the active event's year). Never writes.
    /// </summary>
    Task<string?> GetRegistrationInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the user is an active member of the City Planning team.
    /// </summary>
    Task<bool> IsCityPlanningTeamMemberAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The per-year City Planning settings row as callers see it. Lives on the contracts leaf
/// because it is the return type of <see cref="ICityPlanningService.GetSettingsAsync"/>.
/// </summary>
public record CityPlanningSettingsDto(
    Guid Id,
    int Year,
    bool IsPlacementOpen,
    Instant? OpenedAt,
    Instant? ClosedAt,
    LocalDateTime? PlacementOpensAt,
    LocalDateTime? PlacementClosesAt,
    string? RegistrationInfo,
    string? LimitZoneGeoJson,
    string? OfficialZonesGeoJson,
    bool IsContainerPlacementOpen,
    Instant? ContainerPlacementOpenedAt,
    Instant? ContainerPlacementClosedAt,
    Instant UpdatedAt);
