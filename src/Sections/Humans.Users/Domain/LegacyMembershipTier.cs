namespace Humans.Users.Domain;

/// <summary>
/// The type of the frozen <c>profiles.MembershipTier</c> column, held only until its drop. The live
/// tier is Governance's <c>MembershipTier</c> in <c>governance_membership_tiers</c> (Users
/// decomposition commit 11). Member names are stored as strings: never rename.
/// </summary>
internal enum LegacyMembershipTier
{
    Volunteer = 0,
    Colaborador = 1,
    Asociado = 2
}
