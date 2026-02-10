namespace Humans.Domain.Enums;

/// <summary>
/// Triggers for application state machine transitions.
/// </summary>
public enum ApplicationTrigger
{
    /// <summary>
    /// Start reviewing the application.
    /// </summary>
    StartReview,

    /// <summary>
    /// Approve the application.
    /// </summary>
    Approve,

    /// <summary>
    /// Reject the application.
    /// </summary>
    Reject,

    /// <summary>
    /// Withdraw the application (by applicant).
    /// </summary>
    Withdraw,

    /// <summary>
    /// Request more information (returns to submitted).
    /// </summary>
    RequestMoreInfo
}
