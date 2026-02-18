namespace Humans.Domain.Enums;

/// <summary>
/// Membership tier indicating the level of organizational involvement.
/// Volunteer is the default; Colaborador and Asociado require Board-approved applications.
/// </summary>
public enum MembershipTier
{
    /// <summary>
    /// Default tier. Active volunteer with basic access. No application required.
    /// </summary>
    Volunteer = 0,

    /// <summary>
    /// Active contributor with project/event responsibilities. Requires application + Board vote.
    /// 2-year term synchronized to odd-year cycle endings (Dec 31).
    /// </summary>
    Colaborador = 1,

    /// <summary>
    /// Voting member with governance rights (assemblies, elections). Requires application + Board vote.
    /// 2-year term synchronized to odd-year cycle endings (Dec 31).
    /// </summary>
    Asociado = 2
}
