namespace Humans.Users.Contracts;

/// <summary>
/// The type of the frozen <c>event_participations.Status</c> column. The live status is Tickets'
/// <c>ParticipationStatus</c> in <c>tickets_event_participations</c> (Users decomposition commit 13).
/// The legacy table keeps this copy so its entity never names a Tickets type. Deleted with the table
/// (USERS-19). Member names are stored as strings: never rename.
/// </summary>
public enum LegacyParticipationStatus
{
    NotAttending = 0,
    Ticketed = 1,
    Attended = 2,
    NoShow = 3,
}
