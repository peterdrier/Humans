namespace Humans.Users.Contracts;

/// <summary>
/// The type of the frozen <c>event_participations.Source</c> column; Tickets' <c>ParticipationSource</c>
/// is the live one (Users decomposition commit 13). Deleted with the table (USERS-19). Member names are
/// stored as strings: never rename.
/// </summary>
public enum LegacyParticipationSource
{
    UserDeclared = 0,
    TicketSync = 1,
    AdminBackfill = 2,
}
