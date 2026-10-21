namespace Humans.Users.Domain;

/// <summary>
/// The type of the frozen <c>profiles.ConsentCheckStatus</c> column, held only until its drop. The
/// live status is Consent's <c>ConsentCheckStatus</c> in <c>consent_reviews</c> (Users
/// decomposition commit 12). Member names are stored as strings: never rename.
/// </summary>
internal enum LegacyConsentCheckStatus
{
    Pending = 0,
    Cleared = 1,
    Flagged = 2
}
