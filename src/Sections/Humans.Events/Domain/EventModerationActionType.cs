namespace Humans.Events.Domain;

internal enum EventModerationActionType
{
    Approved = 0,

    /// <summary>Reason required.</summary>
    Rejected = 1,

    /// <summary>Reason required.</summary>
    ResubmitRequested = 2,

    /// <summary>
    /// An in-place edit that kept the status. Not a state transition, so it is appended directly rather than through
    /// <see cref="Event.ApplyModerationAction"/>.
    /// </summary>
    Edited = 3,

    /// <summary>
    /// A moderator pulled an approved event; the status change goes through <see cref="Event.Withdraw"/>.
    /// </summary>
    Withdrawn = 4
}
