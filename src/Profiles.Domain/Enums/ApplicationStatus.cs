namespace Profiles.Domain.Enums;

/// <summary>
/// Represents the status of a membership application.
/// Used with Stateless state machine for workflow management.
/// </summary>
public enum ApplicationStatus
{
    /// <summary>
    /// Application has been submitted and is awaiting review.
    /// </summary>
    Submitted = 0,

    /// <summary>
    /// Application is currently under review by administrators.
    /// </summary>
    UnderReview = 1,

    /// <summary>
    /// Application has been approved.
    /// </summary>
    Approved = 2,

    /// <summary>
    /// Application has been rejected.
    /// </summary>
    Rejected = 3,

    /// <summary>
    /// Application was withdrawn by the applicant.
    /// </summary>
    Withdrawn = 4
}
