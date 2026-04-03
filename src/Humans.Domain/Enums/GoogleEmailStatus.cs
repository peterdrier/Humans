namespace Humans.Domain.Enums;

/// <summary>
/// Status of a user's Google email for Google Workspace sync operations.
/// When Rejected, the system skips enqueuing new outbox events for this user.
/// </summary>
public enum GoogleEmailStatus
{
    /// <summary>
    /// Default state — email has not been validated against Google APIs.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Email has been successfully used with Google APIs.
    /// </summary>
    Valid = 1,

    /// <summary>
    /// Email was permanently rejected by Google APIs (HTTP 400/404).
    /// User must update their Google email before sync events are enqueued.
    /// </summary>
    Rejected = 2
}
