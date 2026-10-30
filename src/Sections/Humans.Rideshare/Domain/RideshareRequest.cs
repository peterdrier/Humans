using NodaTime;

namespace Humans.Rideshare.Domain;

internal sealed class RideshareRequest
{
    public Guid Id { get; init; }

    public Guid UserId { get; set; }

    /// <summary>The year of the event at creation.</summary>
    public int Year { get; set; }

    /// <summary>
    /// The event this row belongs to. Null on rows from before events were tracked, until the
    /// <c>rideshare.event-ids</c> data migration stamps them; until then they belong to the event that run would pick.
    /// </summary>
    public Guid? EventSettingsId { get; set; }

    public RideshareDirection Direction { get; set; }

    /// <summary>Coarse, city-level, e.g. "Lyon, France".</summary>
    public string PickupPlaceLabel { get; set; } = string.Empty;

    public double PickupLatitude { get; set; }

    public double PickupLongitude { get; set; }

    public LocalDate DesiredDate { get; set; }

    public int PartySize { get; set; }

    public LuggageSize LuggageLoad { get; set; }

    public bool CanContributeToFuel { get; set; }

    public string? Notes { get; set; }

    public RequestStatus Status { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }
}
