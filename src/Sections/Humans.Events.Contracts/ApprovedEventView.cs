using System.Globalization;
using NodaTime;

namespace Humans.Events.Contracts;

/// <summary>
/// T-03 — Cached projection of a single approved <c>Event</c> row,
/// flattened with its <c>EventCategory</c> and <c>EventVenue</c>
/// fields so the public guide / API can render without joining at read time.
/// </summary>
/// <remarks>
/// <para>
/// Held in a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// keyed by <see cref="Id"/> inside <c>CachingEventService</c>. Only events in
/// <c>EventStatus.Approved</c> are projected — the moderation dashboard
/// (which needs the live pending count) reads direct DB via
/// <c>GetAllEventsForDashboardAsync</c>. Cache size at the expected ~500
/// approved events × ~2 KB per row ≈ 1 MB — well under the 50 MB budget.
/// </para>
/// <para>
/// Sub-property records embed the joined-in category and venue so consumers
/// don't need to look them up separately at read time. Both are pre-stitched
/// at warm/refresh time from the in-memory category + venue tables.
/// </para>
/// </remarks>
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
    /// <summary>
    /// Expands this approved event into concrete occurrence instants.
    /// Mirrors <c>Event.GetOccurrenceInstants</c>.
    /// </summary>
    public IReadOnlyList<Instant> GetOccurrenceInstants(LocalDate gateOpeningDate, DateTimeZone timeZone)
    {
        if (!IsRecurring || string.IsNullOrWhiteSpace(RecurrenceDays))
            return [StartAt];

        var startLocal = StartAt.InZone(timeZone);

        return RecurrenceDays
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? (int?)d : null)
            .Where(d => d.HasValue)
            .Select(d => gateOpeningDate.PlusDays(d!.Value)
                .At(startLocal.TimeOfDay)
                .InZoneLeniently(timeZone)
                .ToInstant())
            .ToList();
    }
}
