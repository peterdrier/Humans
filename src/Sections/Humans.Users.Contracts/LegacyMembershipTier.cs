namespace Humans.Users.Contracts;

/// <summary>
/// The frozen <c>profiles.MembershipTier</c> type. Governance.Contracts references this project, so the live type
/// can't be named here; the <c>governance.profile-tier</c> copy maps by name. Stored by name: never rename.
/// </summary>
public enum LegacyMembershipTier
{
    Volunteer = 0,
    Colaborador = 1,
    Asociado = 2
}
