namespace Humans.Domain.Enums;

/// <summary>
/// Status of a human's membership in a camp for a specific season.
/// </summary>
public enum CampMemberStatus
{
    /// <summary>The human has requested membership and is awaiting lead approval.</summary>
    Pending = 0,

    /// <summary>The human is an active member of the camp for the season.</summary>
    Active = 1,

    /// <summary>Membership was removed or withdrawn. Soft-deleted row preserved for audit.</summary>
    Removed = 2
}
