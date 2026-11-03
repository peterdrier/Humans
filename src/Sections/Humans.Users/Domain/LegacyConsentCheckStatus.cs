namespace Humans.Users.Domain;

/// <summary>Frozen storage values; names and numeric values stay stable until the column is dropped.</summary>
internal enum LegacyConsentCheckStatus
{
    Pending = 0,
    Cleared = 1,
    Flagged = 2
}
