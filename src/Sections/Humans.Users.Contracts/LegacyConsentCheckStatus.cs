namespace Humans.Users.Contracts;

/// <summary>
/// The type of the frozen <c>profiles.ConsentCheckStatus</c> column. The live status is Consent's
/// <c>ConsentCheckStatus</c> in <c>consent_reviews</c> (Users decomposition commit 12);
/// <c>Users.Contracts</c> cannot name it (Consent references this project), so the legacy field
/// keeps this copy and the <c>consent.profile-review</c> copy maps it by name.
/// Deleted with that migration's retirement PR. Member names are stored as strings: never rename.
/// </summary>
public enum LegacyConsentCheckStatus
{
    Pending = 0,
    Cleared = 1,
    Flagged = 2
}
