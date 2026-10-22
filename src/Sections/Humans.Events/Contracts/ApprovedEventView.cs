using Humans.Events.Services;
using NodaTime;

namespace Humans.Events.Contracts;

/// <summary>
/// One approved event, flattened with its category and venue at cache warm time. Only Approved events are projected;
/// the moderation dashboard reads the database for its live pending count.
/// </summary>
public sealed record ApprovedEventView(
    Guid Id,
    Guid? CampId,
    Guid? GuideSharedVenueId,
    Guid SubmitterUserId,
    Guid CategoryId,
    string CategorySlug,
    string CategoryName,
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
    Instant SubmittedAt,
    Instant LastUpdatedAt)
{
    public IReadOnlyList<Instant> GetOccurrenceInstants(LocalDate gateOpeningDate, DateTimeZone timeZone) =>
        EventRecurrenceDays.GetOccurrenceInstants(
            StartAt, IsRecurring, RecurrenceDays, gateOpeningDate, timeZone);

    internal IReadOnlyList<Instant> GetOccurrenceInstants(LocalDate? gateOpeningDate, DateTimeZone? timeZone) =>
        EventRecurrenceDays.GetOccurrenceInstants(
            StartAt, IsRecurring, RecurrenceDays, gateOpeningDate, timeZone);
}
