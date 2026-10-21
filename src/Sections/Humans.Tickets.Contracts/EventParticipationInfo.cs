using NodaTime;

namespace Humans.Tickets.Contracts;

/// <summary>
/// One member's participation in one event year, as <see cref="ITicketService"/> reads it from
/// <c>tickets_event_participations</c>. No record for a year means unknown / no response.
/// </summary>
/// <param name="UserId">The member, resolved forward through any account merge.</param>
/// <param name="Year">The event year (e.g. 2026).</param>
/// <param name="Status">Current participation status.</param>
/// <param name="Source">How the status was set.</param>
/// <param name="DeclaredAt">When the member self-declared <see cref="ParticipationStatus.NotAttending"/>; null for other sources.</param>
/// <param name="CheckedInAt">First gate arrival; set once with <see cref="ParticipationStatus.Attended"/> and never overwritten.</param>
public sealed record EventParticipationInfo(
    Guid UserId,
    int Year,
    ParticipationStatus Status,
    ParticipationSource Source,
    Instant? DeclaredAt,
    Instant? CheckedInAt)
{
    /// <summary>Holds a ticket for the year: <see cref="ParticipationStatus.Ticketed"/> or <see cref="ParticipationStatus.Attended"/>.</summary>
    public bool HasTicket => Status is ParticipationStatus.Ticketed or ParticipationStatus.Attended;

    /// <summary>The gate-arrival instant when the member is onsite (Attended with a check-in), else null.</summary>
    public Instant? OnsiteSince => Status == ParticipationStatus.Attended ? CheckedInAt : null;
}
