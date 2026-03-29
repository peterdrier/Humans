namespace Humans.Domain.Enums;

/// <summary>
/// Roles that a user can have within a team.
/// </summary>
public enum TeamMemberRole
{
    /// <summary>
    /// Regular team member.
    /// </summary>
    Member = 0,

    /// <summary>
    /// Team coordinator with administrative privileges.
    /// </summary>
    Coordinator = 1
}
