using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Event guide configuration for a single event edition.
/// Links to <see cref="EventSettings"/> for shared date/timezone context.
/// </summary>
public class GuideSettings
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the parent event settings for shared date/timezone context.
    /// </summary>
    public Guid EventSettingsId { get; set; }

    /// <summary>
    /// When camps can start submitting events.
    /// </summary>
    public Instant SubmissionOpenAt { get; set; }

    /// <summary>
    /// When the submission form closes.
    /// </summary>
    public Instant SubmissionCloseAt { get; set; }

    /// <summary>
    /// When the guide goes live to attendees.
    /// </summary>
    public Instant GuidePublishAt { get; set; }

    /// <summary>
    /// Maximum number of events in the printed programme.
    /// </summary>
    public int MaxPrintSlots { get; set; }

    /// <summary>
    /// When this record was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When this record was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    // Navigation properties

    /// <summary>
    /// Navigation property to the parent event settings.
    /// </summary>
    public EventSettings EventSettings { get; set; } = null!;
}
