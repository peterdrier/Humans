namespace Humans.Governance.Domain;

/// <summary>
/// Triggers for application state machine transitions.
/// </summary>
internal enum ApplicationTrigger
{
    Approve,

    Reject,

    Withdraw,

    /// <summary>
    /// Request more information (returns to submitted).
    /// </summary>
    RequestMoreInfo
}
