namespace Humans.Domain.Enums;

/// <summary>
/// Identifies system-managed teams with automatic membership sync.
/// </summary>
public enum SystemTeamType
{
    /// <summary>
    /// User-created team with manual membership management.
    /// </summary>
    None = 0,

    /// <summary>
    /// All volunteers with signed required documents.
    /// Auto-synced based on document compliance.
    /// </summary>
    Volunteers = 1,

    /// <summary>
    /// All users who are metaleads of any team.
    /// Auto-synced based on TeamMember roles.
    /// </summary>
    Metaleads = 2,

    /// <summary>
    /// Board members with active RoleAssignment.
    /// Auto-synced from RoleAssignment table.
    /// </summary>
    Board = 3
}
