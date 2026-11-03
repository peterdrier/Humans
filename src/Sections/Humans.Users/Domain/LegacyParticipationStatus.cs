namespace Humans.Users.Domain;

/// <summary>Frozen storage values; names and numeric values stay stable until the table is dropped.</summary>
internal enum LegacyParticipationStatus
{
    NotAttending = 0,
    Ticketed = 1,
    Attended = 2,
    NoShow = 3,
}
