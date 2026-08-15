namespace Humans.Governance.Domain;

/// <summary>
/// Triggers for application state machine transitions.
/// </summary>
internal enum ApplicationTrigger
{
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
