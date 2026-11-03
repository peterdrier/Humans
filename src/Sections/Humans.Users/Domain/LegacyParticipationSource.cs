namespace Humans.Users.Domain;

/// <summary>Frozen storage values; names and numeric values stay stable until the table is dropped.</summary>
internal enum LegacyParticipationSource
{
    UserDeclared = 0,
    TicketSync = 1,
    AdminBackfill = 2,
}
