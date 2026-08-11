namespace Humans.Onboarding.Services;

/// <summary>
/// Audit-log entity discriminators written by this section, as literals.
/// </summary>
/// <remarks>
/// The funnel annotates and rejects through <c>IUserService</c>, so the entity it names
/// is Profiles' <c>Profile</c> — a type Onboarding does not own and, once Profiles goes
/// to G5, will not be able to spell. <c>nameof</c> over another section's entity compiles
/// today and breaks in a section nobody is editing on the day that section moves, and the
/// string is persisted in <c>audit_log.entity_type</c> either way
/// (<c>memory/code/type-name-as-persisted-string.md</c>).
/// </remarks>
internal static class AuditEntityTypes
{
    internal const string Profile = "Profile";
}
