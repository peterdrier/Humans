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
    /// Team lead with administrative privileges.
    /// </summary>
    Metalead = 1
}
