namespace Humans.Development.Services;

/// <summary>
/// Persisted strings matched exactly when the audit log is read back: never derive them from <c>nameof</c>.
/// Onboarding and Expenses carry the same <c>Profile</c> constant for the same rows.
/// </summary>
internal static class AuditEntityTypes
{
    public const string Profile = "Profile";
}
