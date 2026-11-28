namespace Humans.Base.Enums;

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
    /// All users who are coordinators of any team.
    /// Auto-synced based on TeamMember roles.
    /// </summary>
    Coordinators = 2,

    /// <summary>
    /// All active camp leads across all camps.
    /// Auto-synced based on CampLead assignments.
    /// </summary>
    BarrioLeads = 6
}
