namespace Humans.Users.Contracts;

/// <summary>
/// The type of the frozen <c>profiles.MembershipTier</c> column. The live tier is Governance's
/// <c>MembershipTier</c> in <c>governance_membership_tiers</c> (Users decomposition commit 11);
/// <c>Users.Contracts</c> cannot name it (Governance.Contracts references this project), so the
/// legacy field keeps this copy and the <c>governance.profile-tier</c> copy maps it by name.
/// Deleted with that migration's retirement PR. Member names are stored as strings: never rename.
/// </summary>
public enum LegacyMembershipTier
{
    Volunteer = 0,
    Colaborador = 1,
    Asociado = 2
}
