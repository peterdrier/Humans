namespace Humans.Users.Contracts;

/// <summary>
/// The type of the frozen <c>event_participations.Status</c> column. The live status is Tickets'
/// <c>ParticipationStatus</c> in <c>tickets_event_participations</c> (Users decomposition commit 13).
/// The legacy table keeps this copy so its entity and the obsolete <see cref="UserInfo.EventParticipations"/>
/// never name a Tickets type, and the <c>tickets.event-participations</c> copy maps it by name.
/// Deleted with that migration's retirement PR. Member names are stored as strings: never rename.
/// </summary>
public enum LegacyParticipationStatus
{
    NotAttending = 0,
    Ticketed = 1,
    Attended = 2,
    NoShow = 3,
}
