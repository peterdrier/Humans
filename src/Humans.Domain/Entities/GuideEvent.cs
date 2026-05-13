using System.Globalization;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// A single event submission for the event guide. Exactly one of
/// <see cref="CampId"/> or <see cref="GuideSharedVenueId"/> must be set.
/// State transitions are enforced by domain methods with <see cref="IClock"/>.
/// </summary>
public class GuideEvent
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the camp/barrio (null for individual events at shared venues).
    /// </summary>
    public Guid? CampId { get; set; }

    /// <summary>
    /// FK to the shared venue (null for camp events).
    /// </summary>
    public Guid? GuideSharedVenueId { get; set; }

    /// <summary>
    /// FK to the user who submitted this event.
    /// </summary>
    public Guid SubmitterUserId { get; set; }

    /// <summary>
    /// FK to the event category.
    /// </summary>
    public Guid CategoryId { get; set; }

    /// <summary>
    /// Event title (≤ 80 chars).
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Event description (≤ 300 chars).
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Optional free-text detail within the venue (e.g. "near the fire pit").
    /// </summary>
    public string? LocationNote { get; set; }

    /// <summary>
    /// Event start date/time.
    /// </summary>
    public Instant StartAt { get; set; }

    /// <summary>
    /// Duration in minutes.
    /// </summary>
    public int DurationMinutes { get; set; }

    /// <summary>
    /// Whether this event repeats on multiple days.
    /// </summary>
    public bool IsRecurring { get; set; }

    /// <summary>
    /// Comma-separated day offsets from event start for recurrence (e.g. "0,2,4").
    /// Only meaningful when <see cref="IsRecurring"/> is true.
    /// </summary>
    public string? RecurrenceDays { get; set; }

    /// <summary>
    /// Submitter-assigned priority for print guide selection (1 = highest).
    /// </summary>
    public int PriorityRank { get; set; }

    /// <summary>
    /// Current moderation status.
    /// </summary>
    public GuideEventStatus Status { get; set; }

    /// <summary>
    /// Internal moderator notes (not visible to submitter).
    /// </summary>
    public string? AdminNotes { get; set; }

    /// <summary>
    /// When this event was submitted.
    /// </summary>
    public Instant SubmittedAt { get; set; }

    /// <summary>
    /// When this event was last updated.
    /// </summary>
    public Instant LastUpdatedAt { get; set; }

    // Navigation properties

    /// <summary>
    /// Navigation property to the camp/barrio (null for individual events).
    /// </summary>
    public Camp? Camp { get; set; }

    /// <summary>
    /// Navigation property to the shared venue (null for camp events).
    /// </summary>
    public GuideSharedVenue? GuideSharedVenue { get; set; }

    /// <summary>
    /// Navigation property to the submitter.
    /// </summary>
    public User SubmitterUser { get; set; } = null!;

    /// <summary>
    /// Navigation property to the category.
    /// </summary>
    public EventCategory Category { get; set; } = null!;

    /// <summary>
    /// Navigation property to moderation actions on this event.
    /// </summary>
    public ICollection<ModerationAction> ModerationActions { get; } = new List<ModerationAction>();

    /// <summary>
    /// Navigation property to user favourites of this event.
    /// </summary>
    public ICollection<UserEventFavourite> UserEventFavourites { get; } = new List<UserEventFavourite>();

    // State transition methods

    /// <summary>
    /// Submit for moderation.
    /// </summary>
    public void Submit(IClock clock)
    {
        if (Status is not (GuideEventStatus.Draft or GuideEventStatus.Rejected
            or GuideEventStatus.ResubmitRequested))
            throw new InvalidOperationException($"Cannot submit event in {Status} state");
        var now = clock.GetCurrentInstant();
        Status = GuideEventStatus.Pending;
        SubmittedAt = now;
        LastUpdatedAt = now;
    }

    /// <summary>
    /// Withdraw the submission.
    /// </summary>
    public void Withdraw(IClock clock)
    {
        if (Status is not (GuideEventStatus.Draft or GuideEventStatus.Pending))
            throw new InvalidOperationException($"Cannot withdraw event in {Status} state");
        Status = GuideEventStatus.Withdrawn;
        LastUpdatedAt = clock.GetCurrentInstant();
    }

    /// <summary>
    /// Apply a moderation decision to this event.
    /// </summary>
    public void ApplyModerationAction(ModerationActionType actionType, IClock clock)
    {
        if (Status is not GuideEventStatus.Pending)
            throw new InvalidOperationException($"Cannot moderate event in {Status} state");

        Status = actionType switch
        {
            ModerationActionType.Approved => GuideEventStatus.Approved,
            ModerationActionType.Rejected => GuideEventStatus.Rejected,
            ModerationActionType.ResubmitRequested => GuideEventStatus.ResubmitRequested,
            _ => throw new ArgumentOutOfRangeException(nameof(actionType))
        };
        LastUpdatedAt = clock.GetCurrentInstant();
    }

    /// <summary>
    /// Returns the event start instant plus any recurrence-day offsets.
    /// </summary>
    public IReadOnlyList<Instant> GetOccurrenceInstants()
    {
        if (!IsRecurring || string.IsNullOrWhiteSpace(RecurrenceDays))
            return [StartAt];

        return RecurrenceDays
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dayOffset)
                ? (int?)dayOffset
                : null)
            .Where(dayOffset => dayOffset.HasValue)
            .Select(dayOffset => StartAt.Plus(Duration.FromDays(dayOffset!.Value)))
            .ToList();
    }
}
