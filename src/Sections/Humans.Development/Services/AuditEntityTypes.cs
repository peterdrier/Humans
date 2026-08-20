namespace Humans.Development.Services;

/// <summary>
/// The <c>EntityType</c> discriminators the dev seeders write to the audit log.
/// </summary>
/// <remarks>
/// Persisted strings, matched by exact equality when the audit log is read back, so they are a
/// data contract with rows already in the database — never regenerate them from <c>nameof</c>
/// (<c>memory/code/type-name-as-persisted-string.md</c>). The consent-check entry names Users'
/// <c>Profile</c>, which this section cannot spell since it went internal to <c>Humans.Users</c>
/// (nobodies-collective/Humans#1051); Onboarding and Expenses carry the same constant for the
/// same rows.
/// </remarks>
internal static class AuditEntityTypes
{
    public const string Profile = "Profile";
}
