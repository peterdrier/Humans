namespace Humans.Domain.Enums;

/// <summary>
/// Type of moderation action taken on a guide event submission.
/// </summary>
public enum ModerationActionType
{
    /// <summary>Event approved for publication.</summary>
    Approved = 0,

    /// <summary>Event rejected — reason required.</summary>
    Rejected = 1,

    /// <summary>Moderator requested edits — reason required.</summary>
    ResubmitRequested = 2
}
