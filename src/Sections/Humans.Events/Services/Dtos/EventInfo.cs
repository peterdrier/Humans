using System.Globalization;
using Humans.Events.Domain;
using NodaTime;
using Humans.Events.Contracts;

namespace Humans.Events.Services.Dtos;

/// <summary>
/// T-03 — Cached projection of an <see cref="EventCategory"/> row.
/// </summary>
/// <remarks>
/// Held as a flat <see cref="IReadOnlyList{T}"/> inside
/// <c>CachingEventService</c>. ~10–30 categories × ~100 bytes ≈ trivial.
/// </remarks>
internal sealed record EventCategoryView(
    Guid Id,
    string Name,
    string Slug,
    bool IsSensitive,
    int DisplayOrder,
    bool IsActive);

/// <summary>
/// T-03 — Cached projection of an <see cref="EventVenue"/> row.
/// </summary>
/// <remarks>
/// Held as a flat <see cref="IReadOnlyList{T}"/> inside
/// <c>CachingEventService</c>. ~10–30 venues × ~200 bytes ≈ trivial.
/// </remarks>
internal sealed record EventVenueView(
    Guid Id,
    string Name,
    string? Description,
    string? LocationDescription,
    int DisplayOrder,
    bool IsActive);

/// <summary>
/// Category projection for the admin management list, carrying the linked-event
/// count so the management UI can render usage without exposing the EF nav.
/// </summary>
internal sealed record EventCategoryManageInfo(
    Guid Id,
    string Name,
    string Slug,
    bool IsSensitive,
    int DisplayOrder,
    bool IsActive,
    int EventCount);

/// <summary>
/// Venue projection for the admin management list, carrying the linked-event
/// count so the management UI can render usage without exposing the EF nav.
/// </summary>
internal sealed record EventVenueManageInfo(
    Guid Id,
    string Name,
    string? Description,
    string? LocationDescription,
    int DisplayOrder,
    bool IsActive,
    int EventCount);

/// <summary>
/// A single moderation-history entry projected for the moderation queue,
/// flattened so the presentation layer never touches the EF nav collection.
/// </summary>
internal sealed record EventModerationHistoryInfo(
    Guid ActorUserId,
    EventModerationActionType Action,
    string? Reason,
    Instant CreatedAt);

/// <summary>
/// Full event projection for moderation / dashboard / submissions / export
/// reads. Carries the flattened category and (optional) venue name, status,
/// and — where the source query loaded it — the moderation history. Replaces
/// the <see cref="Event"/> entity on those read surfaces.
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
    /// <summary>
    /// Whether the submitter may edit/resubmit this event — single source:
    /// <see cref="Event.IsEditableBySubmitter"/> (camp events have no Draft stage).
    /// </summary>
    public bool CanEdit => Event.IsEditableBySubmitter(Status, CampId is not null);

    /// <summary>
    /// Whether the submitter may withdraw this event — single source:
    /// <see cref="Event.IsWithdrawableBySubmitter"/>.
    /// </summary>
    public bool CanWithdraw => Event.IsWithdrawableBySubmitter(Status, CampId is not null);

    /// <summary>
    /// Expands this event into concrete occurrence instants. A non-null
    /// <paramref name="dayOffset"/> narrows a recurring event to the single
    /// occurrence on that day offset (non-recurring events ignore it).
    /// Mirrors <see cref="Event.GetOccurrenceInstants"/>.
    /// </summary>
    public IReadOnlyList<Instant> GetOccurrenceInstants(LocalDate gateOpeningDate, DateTimeZone timeZone, int? dayOffset = null)
    {
        if (!IsRecurring || string.IsNullOrWhiteSpace(RecurrenceDays))
            return [StartAt];

        var startLocal = StartAt.InZone(timeZone);

        return RecurrenceDays
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? (int?)d : null)
            .Where(d => d.HasValue && (dayOffset == null || d == dayOffset))
            .Select(d => gateOpeningDate.PlusDays(d!.Value)
                .At(startLocal.TimeOfDay)
                .InZoneLeniently(timeZone)
                .ToInstant())
            .ToList();
    }
}

/// <summary>
/// A favourited event projected for the personal schedule / favourites API —
/// the favourite metadata plus the flattened event projection. A null
/// <see cref="DayOffset"/> means the whole event (every occurrence).
/// </summary>
internal sealed record EventFavouriteInfo(
    Guid Id,
    Guid UserId,
    Guid GuideEventId,
    int? DayOffset,
    Instant CreatedAt,
    EventInfo Event);

/// <summary>
/// Approved events plus the guide settings, projected for export surfaces.
/// </summary>
internal sealed record ApprovedEventsExportInfo(
    IReadOnlyList<EventInfo> Events,
    EventGuideSettingsView? Settings);

/// <summary>
/// A camp's submissions bucketed by moderation status, with the events sorted
/// most-recently-submitted-first — feeds the barrio block on the submitter's
/// "My Submissions" dashboard.
/// </summary>
internal sealed record CampSubmissionsSummary(
    int SubmittedCount,
    int ApprovedCount,
    int PendingCount,
    IReadOnlyList<EventInfo> Events);
