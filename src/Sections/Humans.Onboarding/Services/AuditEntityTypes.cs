namespace Humans.Onboarding.Services;

/// <summary>
/// Persisted in <c>audit_log.entity_type</c>. Names Users' internal <c>Profile</c>, which this section
/// can't reference, so it is a literal.
/// </summary>
internal static class AuditEntityTypes
{
    internal const string Profile = "Profile";
}
