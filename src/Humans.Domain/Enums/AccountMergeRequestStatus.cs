namespace Humans.Domain.Enums;

/// <summary>
/// Status of an account merge request.
/// Stored as string via HasConversion&lt;string&gt;().
/// </summary>
public enum AccountMergeRequestStatus
{
    /// <summary>
    /// Pending admin review.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Accepted — merge completed, source account archived.
    /// </summary>
    Accepted = 1,

    /// <summary>
    /// Rejected — no changes made.
    /// </summary>
    Rejected = 2
}
