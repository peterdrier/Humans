namespace Humans.Users.Contracts;

/// <summary>
/// The frozen <c>event_participations.Status</c> type, so the legacy entity never names a Tickets type; the
/// <c>tickets.event-participations</c> copy maps by name. Stored by name: never rename.
/// </summary>
public enum LegacyParticipationStatus
{
    NotAttending = 0,
    Ticketed = 1,
    Attended = 2,
    NoShow = 3,
}
