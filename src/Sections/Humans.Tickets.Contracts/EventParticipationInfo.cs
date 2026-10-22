using NodaTime;

namespace Humans.Tickets.Contracts;

/// <summary>No record for a year means unknown.</summary>
/// <remarks>
/// <paramref name="UserId"/> is resolved forward through any account merge. <paramref name="DeclaredAt"/> is set
/// only by a self-declared <see cref="ParticipationStatus.NotAttending"/>. <paramref name="CheckedInAt"/> is the
/// first gate arrival and is never overwritten.
/// </remarks>
public sealed record EventParticipationInfo(
    Guid UserId,
    int Year,
    ParticipationStatus Status,
    ParticipationSource Source,
    Instant? DeclaredAt,
    Instant? CheckedInAt)
{
    public bool HasTicket => Status is ParticipationStatus.Ticketed or ParticipationStatus.Attended;

    public Instant? OnsiteSince => Status == ParticipationStatus.Attended ? CheckedInAt : null;
}
