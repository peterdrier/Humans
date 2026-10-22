using Humans.Events.Domain;
using NodaTime;
using Humans.Events.Contracts;

namespace Humans.Events.Services.Dtos;

internal sealed record EventCategoryView(
    Guid Id,
    string Name,
    string Slug,
    bool IsSensitive,
    int DisplayOrder,
    bool IsActive);

internal sealed record EventVenueView(
    Guid Id,
    string Name,
    string? Description,
    string? LocationDescription,
    int DisplayOrder,
    bool IsActive);

/// <summary>Carries the linked-event count so the management UI needs no EF nav.</summary>
internal sealed record EventCategoryManageInfo(
    Guid Id,
    string Name,
    string Slug,
    bool IsSensitive,
    int DisplayOrder,
    bool IsActive,
    int EventCount);

/// <summary>Carries the linked-event count so the management UI needs no EF nav.</summary>
internal sealed record EventVenueManageInfo(
    Guid Id,
    string Name,
    string? Description,
    string? LocationDescription,
    int DisplayOrder,
    bool IsActive,
    int EventCount);

internal sealed record EventModerationHistoryInfo(
    Guid ActorUserId,
    EventModerationActionType Action,
    string? Reason,
    Instant CreatedAt);

/// <summary>
/// The read shape for moderation, dashboard, submissions and export. Moderation history is present only where the
/// query loaded it.
/// </summary>
internal sealed record EventInfo(
    Guid Id,
    Guid? CampId,
    Guid? GuideSharedVenueId,
    Guid SubmitterUserId,
    Guid CategoryId,
    string CategoryName,
    string CategorySlug,
    bool CategoryIsSensitive,
    string? VenueName,
    string Title,
    string Description,
    string? LocationNote,
    string? Host,
    Instant StartAt,
    int DurationMinutes,
    bool IsRecurring,
    string? RecurrenceDays,
    int? PriorityRank,
    EventStatus Status,
    Instant SubmittedAt,
    Instant LastUpdatedAt,
    IReadOnlyList<EventModerationHistoryInfo> ModerationHistory)
{
    public bool CanEdit => Event.IsEditableBySubmitter(Status, CampId is not null);

    public bool CanWithdraw => Event.IsWithdrawableBySubmitter(Status, CampId is not null);

    /// <inheritdoc cref="Event.IsAllDay"/>
    public bool IsAllDay => DurationMinutes == Event.AllDayDurationMinutes;

    /// <summary>A submitter edit after moderation sent the event back (rejected, or edits requested).</summary>
    public bool IsResubmission => Status is EventStatus.Rejected or EventStatus.ResubmitRequested;

    /// <summary>
    /// A non-null <paramref name="dayOffset"/> narrows a recurring event to that day's single occurrence;
    /// non-recurring events ignore it.
    /// </summary>
    public IReadOnlyList<Instant> GetOccurrenceInstants(LocalDate? gateOpeningDate, DateTimeZone? timeZone, int? dayOffset = null) =>
        EventRecurrenceDays.GetOccurrenceInstants(
            StartAt, IsRecurring, RecurrenceDays, gateOpeningDate, timeZone, dayOffset);
}

/// <summary>
/// <see cref="VenueId"/> applies to individual events only and <see cref="PriorityRank"/> to camp events only; the
/// event's own <c>CampId</c> decides which set the service applies.
/// </summary>
internal sealed record EventEdit(
    Guid CategoryId,
    Guid? VenueId,
    string Title,
    string Description,
    Instant StartAt,
    int DurationMinutes,
    string? LocationNote,
    string? Host,
    bool IsRecurring,
    string? RecurrenceDays,
    int? PriorityRank);

/// <summary>
/// A null <see cref="DayOffset"/> means every occurrence. Several day bookmarks retained on a now non-recurring event
/// project as one whole-event target.
/// </summary>
internal sealed record EventFavouriteInfo(
    Guid Id,
    Guid UserId,
    Guid GuideEventId,
    int? DayOffset,
    Instant CreatedAt,
    EventInfo Event);

/// <summary>Unordered: consumers choose the display order.</summary>
internal sealed record CampSubmissionsSummary(
    int SubmittedCount,
    int ApprovedCount,
    int PendingCount,
    IReadOnlyList<EventInfo> Events);
