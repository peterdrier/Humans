using Humans.Events.Services;
using Humans.Base.Attributes;
using NodaTime;
using Humans.Events.Contracts;

namespace Humans.Events.Domain;

/// <summary>
/// Exactly one of <see cref="CampId"/> or <see cref="GuideSharedVenueId"/> must be set.
/// </summary>
internal sealed class Event
{
    public Guid Id { get; init; }
    public Guid? CampId { get; set; }
    public Guid? GuideSharedVenueId { get; set; }
    public Guid SubmitterUserId { get; set; }

    /// <summary>
    /// Settings' event row this entry belongs to. Null only on rows older than the column; the
    /// <c>events.event-ids</c> data migration assigns them, and until then they count as the guide's current event.
    /// </summary>
    public Guid? EventSettingsId { get; set; }
    public Guid CategoryId { get; set; }

    public string Title { get; set; } = string.Empty;

    [MarkdownContent]
    public string Description { get; set; } = string.Empty;

    /// <summary>Free-text detail within the venue, e.g. "near the fire pit".</summary>
    public string? LocationNote { get; set; }

    /// <summary>For individual events, shown in the guide instead of the submitter's name when set.</summary>
    public string? Host { get; set; }
    public Instant StartAt { get; set; }
    public int DurationMinutes { get; set; }
    public bool IsRecurring { get; set; }

    /// <summary>
    /// Comma-separated day offsets from gate opening, e.g. "0,2,4". Only meaningful when <see cref="IsRecurring"/>.
    /// </summary>
    public string? RecurrenceDays { get; set; }

    /// <summary>For print guide selection: 1 = highest, null = unranked.</summary>
    public int? PriorityRank { get; set; }
    public EventStatus Status { get; set; }

    /// <summary>Never shown to the submitter.</summary>
    public string? AdminNotes { get; set; }
    public Instant SubmittedAt { get; set; }
    public Instant LastUpdatedAt { get; set; }

    public EventVenue? EventVenue { get; set; }
    public EventCategory Category { get; set; } = null!;
    public ICollection<EventModerationAction> EventModerationActions { get; } = new List<EventModerationAction>();
    public ICollection<EventFavourite> EventFavourites { get; } = new List<EventFavourite>();

    /// <summary>All-day events are stored as a midnight start + 1440-minute duration.</summary>
    public const int AllDayDurationMinutes = 1440;

    /// <summary>All-day ⇒ midnight start + <see cref="AllDayDurationMinutes"/>.</summary>
    public static (TimeSpan StartTime, int DurationMinutes) ResolveAllDaySchedule(
        bool isAllDay, TimeSpan startTime, int durationMinutes) =>
        isAllDay ? (TimeSpan.Zero, AllDayDurationMinutes) : (startTime, durationMinutes);

    public bool IsAllDay => DurationMinutes == AllDayDurationMinutes;

    /// <summary>Camp events have no Draft stage; individual events may also be edited while Draft.</summary>
    public static bool IsEditableBySubmitter(EventStatus status, bool isCampEvent) =>
        status is EventStatus.Pending or EventStatus.Rejected or EventStatus.ResubmitRequested
        || (!isCampEvent && status is EventStatus.Draft);

    /// <summary>Camp events have no Draft stage.</summary>
    public static bool IsWithdrawableBySubmitter(EventStatus status, bool isCampEvent) =>
        status is EventStatus.Pending or EventStatus.Approved
        || (!isCampEvent && status is EventStatus.Draft);

    /// <summary>
    /// True when the entry belongs to <paramref name="eventSettingsId"/>. An unassigned entry, or a null event (the
    /// guide is not configured yet), matches, so nothing disappears before the data migration runs.
    /// </summary>
    public bool IsInEvent(Guid? eventSettingsId) =>
        eventSettingsId is null || EventSettingsId is null || EventSettingsId == eventSettingsId;

    /// <inheritdoc cref="IsEditableBySubmitter(EventStatus, bool)"/>
    public bool CanBeEditedBySubmitter => IsEditableBySubmitter(Status, CampId is not null);

    /// <inheritdoc cref="IsWithdrawableBySubmitter(EventStatus, bool)"/>
    public bool CanBeWithdrawnBySubmitter => IsWithdrawableBySubmitter(Status, CampId is not null);

    public void Submit(IClock clock)
    {
        if (Status is not (EventStatus.Draft or EventStatus.Rejected
            or EventStatus.ResubmitRequested))
            throw new InvalidOperationException($"Cannot submit event in {Status} state");
        var now = clock.GetCurrentInstant();
        Status = EventStatus.Pending;
        SubmittedAt = now;
        LastUpdatedAt = now;
    }

    /// <summary>Requeues an edited event, retaining the submission time while it is pending.</summary>
    public void Resubmit(IClock clock)
    {
        if (Status is EventStatus.Pending)
        {
            LastUpdatedAt = clock.GetCurrentInstant();
        }
        else if (Status is EventStatus.Approved)
        {
            var now = clock.GetCurrentInstant();
            Status = EventStatus.Pending;
            SubmittedAt = now;
            LastUpdatedAt = now;
        }
        else
        {
            Submit(clock);
        }
    }

    /// <summary>From Pending or Approved, plus Draft for individual events.</summary>
    public void Withdraw(IClock clock)
    {
        if (!CanBeWithdrawnBySubmitter)
            throw new InvalidOperationException($"Cannot withdraw event in {Status} state");
        Status = EventStatus.Withdrawn;
        LastUpdatedAt = clock.GetCurrentInstant();
    }

    /// <summary>The caller persists via <c>UpdateAndResubmitAsync</c>.</summary>
    public void ApplyIndividualEdit(
        Guid categoryId, Guid venueId, string title, string description,
        Instant startAt, int durationMinutes, string? locationNote, string? host,
        bool isRecurring, string? recurrenceDays)
    {
        CategoryId = categoryId;
        GuideSharedVenueId = venueId;
        Title = title;
        Description = description;
        StartAt = startAt;
        DurationMinutes = durationMinutes;
        LocationNote = locationNote;
        Host = host;
        IsRecurring = isRecurring;
        RecurrenceDays = isRecurring ? recurrenceDays : null;
    }

    /// <summary>The caller persists via <c>UpdateAndResubmitAsync</c>.</summary>
    public void ApplyBarrioEdit(
        Guid categoryId, string title, string description,
        Instant startAt, int durationMinutes, string? locationNote, string? host,
        bool isRecurring, string? recurrenceDays, int? priorityRank)
    {
        CategoryId = categoryId;
        Title = title;
        Description = description;
        StartAt = startAt;
        DurationMinutes = durationMinutes;
        LocationNote = locationNote;
        Host = host;
        IsRecurring = isRecurring;
        RecurrenceDays = isRecurring ? recurrenceDays : null;
        PriorityRank = priorityRank;
    }

    public void ApplyModerationAction(EventModerationActionType actionType, IClock clock)
    {
        if (Status is not EventStatus.Pending)
            throw new InvalidOperationException($"Cannot moderate event in {Status} state");

        Status = actionType switch
        {
            EventModerationActionType.Approved => EventStatus.Approved,
            EventModerationActionType.Rejected => EventStatus.Rejected,
            EventModerationActionType.ResubmitRequested => EventStatus.ResubmitRequested,
            _ => throw new ArgumentOutOfRangeException(nameof(actionType))
        };
        LastUpdatedAt = clock.GetCurrentInstant();
    }

    /// <summary>
    /// A non-null <paramref name="dayOffset"/> narrows a recurring event to that day's single occurrence;
    /// non-recurring events ignore it.
    /// </summary>
    public IReadOnlyList<Instant> GetOccurrenceInstants(LocalDate? gateOpeningDate, DateTimeZone? timeZone, int? dayOffset = null) =>
        EventRecurrenceDays.GetOccurrenceInstants(
            StartAt, IsRecurring, RecurrenceDays, gateOpeningDate, timeZone, dayOffset);
}
