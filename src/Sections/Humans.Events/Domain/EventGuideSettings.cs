using NodaTime;

namespace Humans.Events.Domain;

internal sealed class EventGuideSettings
{
    public Guid Id { get; init; }

    /// <summary>Settings' event row, for shared date and time zone context.</summary>
    public Guid EventSettingsId { get; set; }

    public Instant SubmissionOpenAt { get; set; }

    public Instant SubmissionCloseAt { get; set; }

    /// <summary>When the guide goes live to attendees.</summary>
    public Instant GuidePublishAt { get; set; }

    /// <summary>Maximum events in the printed programme.</summary>
    public int MaxPrintSlots { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    public bool IsSubmissionOpenAt(Instant now) =>
        now >= SubmissionOpenAt && now <= SubmissionCloseAt;
}
