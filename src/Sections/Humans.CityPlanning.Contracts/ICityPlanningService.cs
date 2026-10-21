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
/// <para>Barrio placement (peterdrier/Humans#2102): <c>PlacementState</c> is the window in effect now —
/// the manual <c>PlacementOverride</c> when set (true = forced open, false = forced closed), else the
/// schedule evaluated in the active event's time zone. <c>OpenedAt</c>/<c>ClosedAt</c> are when an admin
/// last forced it open/closed. The three <c>*Offset</c> values are days from gate opening (negative =
/// before): the first day open, the last day open, and the last day a lead may still adjust an existing
/// polygon. The matching <c>*On</c> dates are those days on the calendar — null when the offset is unset
/// or the row's year is not the active event's.</para>
/// </summary>
public record CityPlanningSettingsDto(
    Guid Id,
    int Year,
    BarrioPlacementState PlacementState,
    bool? PlacementOverride,
    Instant? OpenedAt,
    Instant? ClosedAt,
    int? PlacementOpensOffset,
    int? PlacementClosesOffset,
    int? PlacementChangeableUntilOffset,
    LocalDate? PlacementOpensOn,
    LocalDate? PlacementClosesOn,
    LocalDate? PlacementChangeableUntilOn,
    string? RegistrationInfo,
    string? LimitZoneGeoJson,
    string? OfficialZonesGeoJson,
    bool IsContainerPlacementOpen,
    Instant? ContainerPlacementOpenedAt,
    Instant? ContainerPlacementClosedAt,
    Instant UpdatedAt);

/// <summary>
/// Where the barrio placement window stands for a camp lead. City-planning team members and
/// CampAdmin/Admin edit regardless.
/// </summary>
public enum BarrioPlacementState
{
    /// <summary>No lead may create or change a polygon.</summary>
    Closed,

    /// <summary>A lead may place their own camp season's polygon and edit it.</summary>
    Open,

    /// <summary>A lead may still adjust their existing polygon but not create a new one.</summary>
    Changeable,
}
