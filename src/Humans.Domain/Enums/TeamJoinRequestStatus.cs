namespace Humans.Domain.Enums;

/// <summary>
/// Status of a team join request.
/// </summary>
public enum TeamJoinRequestStatus
{
    /// <summary>
    /// Request is pending review.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Request has been approved.
    /// </summary>
    Approved = 1,

    /// <summary>
    /// Request has been rejected.
    /// </summary>
    Rejected = 2,

    /// <summary>
    /// Request was withdrawn by the applicant.
    /// </summary>
    Withdrawn = 3
}
