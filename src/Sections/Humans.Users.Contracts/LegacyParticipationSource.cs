namespace Humans.Users.Contracts;

/// <summary>
/// The frozen <c>event_participations.Source</c> type; the <c>tickets.event-participations</c> copy maps by name.
/// Stored by name: never rename.
/// </summary>
public enum LegacyParticipationSource
{
    UserDeclared = 0,
    TicketSync = 1,
    AdminBackfill = 2,
}
