using NodaTime;

namespace Humans.CityPlanning.Domain;

internal sealed class CityPlanningSettings
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The season year this row applies to. Unique.</summary>
    public int Year { get; init; }

    /// <summary>
    /// The admins' manual override of the barrio placement schedule: null follows the schedule,
    /// true forces placement open, false forces it closed. Persisted in the column that held the
    /// old manual <c>IsPlacementOpen</c> flag, so a row's existing true/false carries over as a
    /// forced open/closed (peterdrier/Humans#2102).
    /// </summary>
    public bool? PlacementOverride { get; set; }

    /// <summary>When an admin last forced placement open.</summary>
    public Instant? OpenedAt { get; set; }

    /// <summary>When an admin last forced placement closed.</summary>
    public Instant? ClosedAt { get; set; }

    /// <summary>First day of the barrio placement window, in days from the active event's gate opening (negative = before). Null = no scheduled opening.</summary>
    public int? PlacementOpensOffset { get; set; }

    /// <summary>Last day of the barrio placement window, in days from gate opening. Null = the window does not close on its own.</summary>
    public int? PlacementClosesOffset { get; set; }

    /// <summary>Last day after the window closes on which a lead may still adjust an existing polygon, in days from gate opening. Null = no adjust-only period.</summary>
    public int? PlacementChangeableUntilOffset { get; set; }

    public bool IsContainerPlacementOpen { get; set; }
    public Instant? ContainerPlacementOpenedAt { get; set; }
    public Instant? ContainerPlacementClosedAt { get; set; }

    /// <summary>
    /// Retired display-only scheduled open time, superseded by <see cref="PlacementOpensOffset"/>
    /// (peterdrier/Humans#2102). Nothing reads or writes it; the property stays only so the column is
    /// not dropped without the per-case approval memory/architecture/no-drops-until-prod-verified.md requires.
    /// </summary>
    [Obsolete("Retired by peterdrier/Humans#2102 — use PlacementOpensOffset. Kept only until its column drop is approved.")]
    public LocalDateTime? PlacementOpensAt { get; set; }

    /// <summary>
    /// Retired display-only scheduled close time, superseded by <see cref="PlacementClosesOffset"/>
    /// (peterdrier/Humans#2102). Kept only so the column is not dropped without approval.
    /// </summary>
    [Obsolete("Retired by peterdrier/Humans#2102 — use PlacementClosesOffset. Kept only until its column drop is approved.")]
    public LocalDateTime? PlacementClosesAt { get; set; }

    /// <summary>Admin-editable markdown content shown at the top of the barrio registration page. Null/empty = hidden.</summary>
    public string? RegistrationInfo { get; set; }

    /// <summary>GeoJSON FeatureCollection defining the visual site boundary. Null until uploaded.</summary>
    public string? LimitZoneGeoJson { get; set; }

    /// <summary>GeoJSON FeatureCollection of official zones (read-only overlay). Each Feature must have a "name" property. Null until uploaded.</summary>
    public string? OfficialZonesGeoJson { get; set; }

    public Instant UpdatedAt { get; set; }
}
