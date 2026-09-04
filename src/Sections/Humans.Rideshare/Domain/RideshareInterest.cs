using NodaTime;

namespace Humans.Rideshare.Domain;

/// <summary>
/// The "I'm interested" / "I can take you" log entry. Always anchored to the
/// <see cref="Trip"/> whose seat it would consume; optionally records the
/// <see cref="Request"/> it answered (the driver-answers-a-pin path).
/// A signal, never a reservation.
/// </summary>
internal sealed class RideshareInterest
{
    public Guid Id { get; init; }

    /// <summary>Who expressed the interest. Bare cross-section reference — no FK, no navigation.</summary>
    public Guid FromUserId { get; set; }

    public Guid TripId { get; set; }

    public Guid? RequestId { get; set; }

    /// <summary>How many people this interest is for.</summary>
    public int Seats { get; set; }

    public string? Message { get; set; }

    public InterestStatus Status { get; set; }

    public Instant CreatedAt { get; init; }

    /// <summary>When the posting owner accepted or declined.</summary>
    public Instant? RespondedAt { get; set; }

    // Navigation properties (intra-section)

    public RideshareTrip Trip { get; set; } = null!;

    public RideshareRequest? Request { get; set; }
}
