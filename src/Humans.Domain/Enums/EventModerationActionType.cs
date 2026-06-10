namespace Humans.Domain.Enums;

/// <summary>
/// Type of moderation action taken on a guide event submission.
/// </summary>
public enum EventModerationActionType
{
    /// <summary>Event approved for publication.</summary>
    Approved = 0,

    /// <summary>Event rejected — reason required.</summary>
    Rejected = 1,

    /// <summary>Moderator requested edits — reason required.</summary>
    ResubmitRequested = 2,

    /// <summary>
    /// An admin / moderator edited the event's fields in place without
    /// changing its status (the "edit in a pinch" path). Recorded for audit;
    /// not a state-transition decision, so it is appended directly rather than
    /// through <see cref="Entities.Event.ApplyModerationAction"/>.
    /// </summary>
    Edited = 3
}
