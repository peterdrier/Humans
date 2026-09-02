using NodaTime;

namespace Humans.Rideshare.Domain;

/// <summary>
/// One driver's leg: their city ↔ the burn, with seats and cargo space on offer.
/// The burn end comes from <see cref="RideshareSettings"/>; only the member end is stored here.
/// </summary>
internal sealed class RideshareTrip
{
    public Guid Id { get; init; }

    /// <summary>The driver. Bare cross-section reference — no FK, no navigation.</summary>
    public Guid UserId { get; set; }

    /// <summary>The burn year the trip belongs to (the active year at creation).</summary>
    public int Year { get; set; }

    public RideshareDirection Direction { get; set; }

    /// <summary>The non-burn end, e.g. "Berlin". Coarse, city-level.</summary>
    public string MemberPlaceLabel { get; set; } = string.Empty;

    public double MemberLatitude { get; set; }

    public double MemberLongitude { get; set; }

    /// <summary>Ordered vias in travel order as a JSON array of <c>{label, latitude, longitude}</c>; null/empty = direct.</summary>
    public string? WaypointsJson { get; set; }

    /// <summary>Stored GeoJSON geometry (LineString) computed at save; null when routing was unavailable.</summary>
    public string? RouteGeoJson { get; set; }

    /// <summary>First day of travel.</summary>
    public LocalDate DepartureDate { get; set; }

    /// <summary>1 = same-day; 2+ = multi-day.</summary>
    public int ExpectedDurationDays { get; set; }

    public string? OvernightPlan { get; set; }

    public VehicleType VehicleType { get; set; }

    public int SeatsOffered { get; set; }

    public LuggageSize LuggageCapacity { get; set; }

    public string? CapacityNote { get; set; }

    public string? Restrictions { get; set; }

    public bool WillingToDetour { get; set; }

    public CostSharing CostSharing { get; set; }

    public string? CostNote { get; set; }

    /// <summary>Soft link to the paired return leg — display only, no FK.</summary>
    public Guid? LinkedTripId { get; set; }

    public TripStatus Status { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    // Navigation properties (intra-section)

    public ICollection<RideshareInterest> Interests { get; set; } = new List<RideshareInterest>();
}
