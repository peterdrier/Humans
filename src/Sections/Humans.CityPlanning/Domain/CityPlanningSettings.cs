using NodaTime;

namespace Humans.CityPlanning.Domain;

internal sealed class CityPlanningSettings
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Unique.</summary>
    public int Year { get; init; }

    /// <summary>
    /// Null follows the schedule; true forces placement open, false closed. Mapped to the old <c>IsPlacementOpen</c>
    /// column, so existing rows' values carry over as forced open/closed.
    /// </summary>
    public bool? PlacementOverride { get; set; }

    /// <summary>When an admin last forced placement open.</summary>
    public Instant? OpenedAt { get; set; }

    /// <summary>When an admin last forced placement closed.</summary>
    public Instant? ClosedAt { get; set; }

    /// <summary>Days from the active event's gate opening (negative = before). Null = no scheduled opening.</summary>
    public int? PlacementOpensOffset { get; set; }

    /// <summary>Days from gate opening. Null = the window does not close on its own.</summary>
    public int? PlacementClosesOffset { get; set; }

    /// <summary>Last day a lead may still adjust an existing polygon, in days from gate opening. Null = no adjust-only period.</summary>
    public int? PlacementChangeableUntilOffset { get; set; }

    public bool IsContainerPlacementOpen { get; set; }
    public Instant? ContainerPlacementOpenedAt { get; set; }
    public Instant? ContainerPlacementClosedAt { get; set; }

    /// <summary>Unread; kept only until its column drop is approved.</summary>
    [Obsolete("Retired by peterdrier/Humans#2102 — use PlacementOpensOffset. Kept only until its column drop is approved.")]
    public LocalDateTime? PlacementOpensAt { get; set; }

    /// <summary>Unread; kept only until its column drop is approved.</summary>
    [Obsolete("Retired by peterdrier/Humans#2102 — use PlacementClosesOffset. Kept only until its column drop is approved.")]
    public LocalDateTime? PlacementClosesAt { get; set; }

    /// <summary>Markdown shown at the top of the barrio registration page. Null/empty = hidden.</summary>
    public string? RegistrationInfo { get; set; }

    /// <summary>The visual site boundary.</summary>
    public string? LimitZoneGeoJson { get; set; }

    /// <summary>A read-only overlay. Each Feature must have a "name" property.</summary>
    public string? OfficialZonesGeoJson { get; set; }

    public Instant UpdatedAt { get; set; }
}
