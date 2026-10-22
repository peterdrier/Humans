namespace Humans.Users.Contracts;

/// <summary>
/// The frozen <c>profiles.ConsentCheckStatus</c> type. Consent references this project, so the live type can't be
/// named here; the <c>consent.profile-review</c> copy maps by name. Stored by name: never rename.
/// </summary>
public enum LegacyConsentCheckStatus
{
    Pending = 0,
    Cleared = 1,
    Flagged = 2
}
