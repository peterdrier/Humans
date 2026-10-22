namespace Humans.Camps.Contracts;

public enum CampMemberStatus
{
    /// <summary>Awaiting lead approval.</summary>
    Pending = 0,

    Active = 1,

    /// <summary>Soft-delete tombstone kept for audit; a re-request creates a new row.</summary>
    Removed = 2
}
