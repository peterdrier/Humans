namespace Humans.Governance.Contracts;

/// <summary>
/// Computed, never stored.
/// </summary>
public enum MembershipStatus
{
    None = 0,

    Pending = 1,

    Active = 2,

    /// <summary>
    /// Member is missing required consent records and has lost access.
    /// </summary>
    Inactive = 3,

    Suspended = 4
}
