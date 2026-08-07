namespace Humans.Domain.Enums;

/// <summary>
/// Lifecycle state of a <see cref="Entities.Profile"/>. Issue #635 (§15i):
/// stored as a nullable string column added by the additive migration. Existing
/// rows hold <c>null</c> until first read; <c>CachingUserService</c> lazily
/// computes the correct value from required-field presence and persists it via
/// the repository so the next read is canonical. Eventually every row is touched
/// and populated; the column is later promoted to <c>NOT NULL</c> in a separate
/// schema change after soak.
/// </summary>
public enum ProfileState
{
    /// <summary>
    /// Profile row exists but core required fields (BurnerName, FirstName,
    /// LastName) are blank — typical for users created via contact import or
    /// the Stub Profile invariant before they complete signup.
    /// </summary>
    Stub = 0,

    /// <summary>
    /// All required fields populated and the profile is not suspended.
    /// </summary>
    Active = 1,

    /// <summary>
    /// Profile has been suspended for missing required consents.
    /// </summary>
    Suspended = 2,

    /// <summary>
    /// Profile has been explicitly suspended by an administrator. Kept separate
    /// from <see cref="Suspended"/> so consent completion cannot clear an admin
    /// suspension.
    /// </summary>
    AdminSuspended = 3,
}
