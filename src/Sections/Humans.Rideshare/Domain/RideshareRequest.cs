using NodaTime;

namespace Humans.Rideshare.Domain;

/// <summary>One rider's need: a pickup point and a desired date, with party size and cargo.</summary>
internal sealed class RideshareRequest
{
    public Guid Id { get; init; }

    /// <summary>The rider. Bare cross-section reference — no FK, no navigation.</summary>
    public Guid UserId { get; set; }

    /// <summary>The burn year the request belongs to (the active year at creation).</summary>
    public int Year { get; set; }

    public RideshareDirection Direction { get; set; }

    /// <summary>Coarse, city-level pickup point, e.g. "Lyon, France".</summary>
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
