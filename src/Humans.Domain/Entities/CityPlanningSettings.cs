using NodaTime;

namespace Humans.Domain.Entities;

public class CityPlanningSettings
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The season year this row applies to. Unique.</summary>
    public int Year { get; init; }

    public bool IsPlacementOpen { get; set; }
    public Instant? OpenedAt { get; set; }
    public Instant? ClosedAt { get; set; }

    /// <summary>Informational scheduled open time shown in help modal. Not enforced.</summary>
    public LocalDateTime? PlacementOpensAt { get; set; }

    /// <summary>Informational scheduled close time shown in help modal. Not enforced.</summary>
    public LocalDateTime? PlacementClosesAt { get; set; }

    /// <summary>Admin-editable markdown content shown at the top of the barrio registration page. Null/empty = hidden.</summary>
    public string? RegistrationInfo { get; set; }

    /// <summary>GeoJSON FeatureCollection defining the visual site boundary. Null until uploaded.</summary>
    public string? LimitZoneGeoJson { get; set; }

    /// <summary>GeoJSON FeatureCollection of official zones (read-only overlay). Each Feature must have a "name" property. Null until uploaded.</summary>
    public string? OfficialZonesGeoJson { get; set; }

    public Instant UpdatedAt { get; set; }
}
