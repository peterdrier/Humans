using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.CityPlanning.Contracts;

public interface ICityPlanningService : IApplicationService
{
    /// <summary>
    /// Defaults to the active event's year. Never writes: a year with no row reads as unpersisted defaults
    /// (<c>Id</c> = <see cref="Guid.Empty"/>). Null only when the year is omitted and no event is active.
    /// </summary>
    Task<CityPlanningSettingsDto?> GetSettingsAsync(CancellationToken cancellationToken = default, int? year = null);

    /// <summary>Keyed to the highest open season, else the active event's year. Never writes.</summary>
    Task<string?> GetRegistrationInfoAsync(CancellationToken cancellationToken = default);

    Task<bool> IsCityPlanningTeamMemberAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>PlacementState</c> is the window in effect now: <c>PlacementOverride</c> when set (true = forced open,
/// false = forced closed), else the schedule in the active event's time zone. <c>OpenedAt</c>/<c>ClosedAt</c>
/// are when an admin last forced it. The <c>*Offset</c> values are days from gate opening (negative = before):
/// first open day, last open day, last day a lead may adjust an existing polygon. The matching <c>*On</c>
/// dates are null when the offset is unset or the row's year is not the active event's.
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
