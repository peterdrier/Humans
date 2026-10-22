namespace Humans.Users.Contracts;

/// <summary>Stored by name: never rename.</summary>
public enum AccountMergeRequestStatus
{
    Pending = 0,

    /// <summary>Merge completed; the source account is archived.</summary>
    Accepted = 1,

    /// <summary>No changes made. Dismiss reuses it.</summary>
    Rejected = 2
}
