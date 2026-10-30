using NodaTime;

namespace Humans.Rideshare.Domain;

/// <summary>Per-event singleton: the routable burn end of every trip, and the travel windows.</summary>
internal sealed class RideshareSettings
{
    public Guid Id { get; init; }

    public int Year { get; set; }

    /// <summary>
    /// The event this row belongs to. Null on rows from before events were tracked, until the
    /// <c>rideshare.event-ids</c> data migration stamps them; until then they belong to the event that run would pick.
    /// </summary>
    public Guid? EventSettingsId { get; set; }

    public string DestinationLabel { get; set; } = string.Empty;

    public double DestinationLatitude { get; set; }

    public double DestinationLongitude { get; set; }

    public LocalDate InboundWindowStart { get; set; }

    public LocalDate InboundWindowEnd { get; set; }

    public LocalDate OutboundWindowStart { get; set; }

    public LocalDate OutboundWindowEnd { get; set; }

    public Instant UpdatedAt { get; set; }
}
