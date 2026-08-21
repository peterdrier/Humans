namespace Humans.Users.Contracts;

/// <summary>
/// Superseded by <see cref="UserState"/> on <c>User.State</c>, which now stores suspension
/// directly. Unreferenced, and the <c>profiles.state</c> column it described is dropped —
/// the type itself is deleted in a follow-up.
/// </summary>
[Obsolete("Superseded by UserState on User.State — nobodies-collective/Humans#844. Unreferenced; profiles.state is dropped and this type is deleted in a follow-up.", DiagnosticId = "HUM_PROFILE_STATE", UrlFormat = "https://github.com/nobodies-collective/Humans/issues/844")]
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
