namespace Profiles.Domain.Enums;

/// <summary>
/// Represents the computed membership status of a member.
/// This is calculated from RoleAssignments and ConsentRecords.
/// </summary>
public enum MembershipStatus
{
    /// <summary>
    /// Member has no active roles.
    /// </summary>
    None = 0,

    /// <summary>
    /// Member is pending approval or has incomplete requirements.
    /// </summary>
    Pending = 1,

    /// <summary>
    /// Member has active roles and valid consent for all required documents.
    /// </summary>
    Active = 2,

    /// <summary>
    /// Member is missing required consent records and has lost access.
    /// </summary>
    Inactive = 3,

    /// <summary>
    /// Member has been suspended by an administrator.
    /// </summary>
    Suspended = 4
}
