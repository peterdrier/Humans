namespace Humans.Events.Contracts;

public enum EventStatus
{
    /// <summary>Saved but not submitted.</summary>
    Draft = 0,

    Pending = 1,

    /// <summary>Visible in the published guide.</summary>
    Approved = 2,

    /// <summary>The submitter is notified with the reason.</summary>
    Rejected = 3,

    ResubmitRequested = 4,

    /// <summary>By the submitter or a moderator; hidden from the published guide.</summary>
    Withdrawn = 5
}
