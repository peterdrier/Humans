namespace Humans.Domain.Enums;

/// <summary>
/// Status of an event guide submission through the moderation workflow.
/// </summary>
public enum GuideEventStatus
{
    /// <summary>Saved but not yet submitted for review.</summary>
    Draft = 0,

    /// <summary>Submitted and awaiting moderation.</summary>
    Pending = 1,

    /// <summary>Approved by a moderator — visible in the published guide.</summary>
    Approved = 2,

    /// <summary>Rejected by a moderator — submitter notified with reason.</summary>
    Rejected = 3,

    /// <summary>Moderator requested edits before approval.</summary>
    ResubmitRequested = 4,

    /// <summary>Withdrawn by the submitter.</summary>
    Withdrawn = 5
}
