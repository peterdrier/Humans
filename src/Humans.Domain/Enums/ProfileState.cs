namespace Humans.Domain.Enums;

/// <summary>
/// Lifecycle state of a <see cref="Entities.Profile"/>. Issue #635 (§15i):
/// stored as a nullable string column added by the additive migration. Existing
/// rows hold <c>null</c> until first read; <c>CachingProfileService</c> lazily
/// computes the correct value from <c>IsSuspended</c> + required-field presence
/// and persists it via the repository so the next read is canonical. Eventually
/// every row is touched and populated; the column is later promoted to
/// <c>NOT NULL</c> in a separate schema change after soak.
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
    /// Profile has been administratively suspended. Replaces the
    /// <c>IsSuspended = true</c> write path; the underlying
    /// <see cref="Entities.Profile.IsSuspended"/> column stays per
    /// <c>memory/architecture/no-drops-until-prod-verified.md</c> until a
    /// follow-up PR drops it after prod soak.
    /// </summary>
    Suspended = 2,
}
