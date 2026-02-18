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
    /// All users who are leads of any team.
    /// Auto-synced based on TeamMember roles.
    /// </summary>
    Leads = 2,

    /// <summary>
    /// Board members with active RoleAssignment.
    /// Auto-synced from RoleAssignment table.
    /// </summary>
    Board = 3,

    /// <summary>
    /// Asociados (voting members) with approved applications.
    /// Auto-synced based on Application status.
    /// </summary>
    Asociados = 4,

    /// <summary>
    /// Colaboradors (active contributors) with approved applications.
    /// Auto-synced based on Application status.
    /// </summary>
    Colaboradors = 5
}
