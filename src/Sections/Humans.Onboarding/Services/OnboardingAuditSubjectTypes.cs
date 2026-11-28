namespace Humans.Onboarding.Services;

/// <summary>
/// Persisted in <c>audit_log_entries.SubjectType</c>. Names Users' internal <c>Profile</c>, which this section
/// can't reference, so it is a literal.
/// </summary>
internal static class OnboardingAuditSubjectTypes
{
    internal const string Profile = "Profile";
}
