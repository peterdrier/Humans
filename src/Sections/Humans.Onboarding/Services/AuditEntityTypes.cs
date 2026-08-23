namespace Humans.Onboarding.Services;

/// <summary>
/// Audit-log entity discriminators written by this section, as literals.
/// </summary>
/// <remarks>
/// The funnel annotates and rejects through <c>IUserService</c>, so the entity it names
/// is Users' <c>Profile</c> — a type Onboarding does not own and, now that Users is its
/// own G5 project, cannot spell: the entity is internal to <c>Humans.Users</c>. The value
/// is persisted in <c>audit_log.entity_type</c>, so a literal is what it would have to be
/// regardless (<c>memory/code/type-name-as-persisted-string.md</c>).
/// </remarks>
internal static class AuditEntityTypes
{
    internal const string Profile = "Profile";
}
