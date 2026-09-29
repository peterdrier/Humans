using Humans.Base.Extensions;
using Humans.Events.Domain;
using Humans.Events.Services.Dtos;
using NodaTime;

namespace Humans.Events.Services;

/// <summary>Applies validated barrio bulk-upload rows through the lifecycle callbacks.</summary>
internal static class EventBulkImporter
{
    public static async Task<BulkImportResult> ImportAsync(
        Guid campId, Guid submitterUserId, IReadOnlyList<BulkCsvRow> rows,
        IReadOnlyList<EventCategory> categories, IReadOnlyList<Event> existingEvents,
        LocalDate gateOpeningDate, int eventEndOffset, DateTimeZone timeZone, IClock clock,
        Func<Event, CancellationToken, Task> updateAndResubmit,
        Func<Event, CancellationToken, Task> submit,
        CancellationToken ct)
    {
        var created = 0;
        var updated = 0;
        foreach (var row in rows)
        {
            var category = categories.First(category =>
                string.Equals(category.Name, row.Category, StringComparison.OrdinalIgnoreCase));
            var startAt = (NodaTime.Text.LocalDatePattern.Iso.Parse(row.Date).Value
                + DateFormattingExtensions.TimeOfDayPattern.Parse(row.StartTime).Value)
                .InZoneLeniently(timeZone).ToInstant();
            var recurrenceOffsets = row.IsRecurring && !string.IsNullOrEmpty(row.RecurrenceDays)
                ? EventRecurrenceDays.DisplayDaysToOffsets(row.RecurrenceDays, gateOpeningDate, eventEndOffset)
                : null;

            if (row.Id is not { } eventId)
            {
                var newEvent = new Event
                {
                    Id = Guid.NewGuid(),
                    CampId = campId,
                    SubmitterUserId = submitterUserId,
                    CategoryId = category.Id,
                    Title = row.Title,
                    Description = row.Description,
                    LocationNote = string.IsNullOrEmpty(row.LocationNote) ? null : row.LocationNote,
                    Host = string.IsNullOrEmpty(row.Host) ? null : row.Host,
                    StartAt = startAt,
                    DurationMinutes = row.DurationMinutes,
                    IsRecurring = row.IsRecurring,
                    RecurrenceDays = recurrenceOffsets,
                    PriorityRank = row.PriorityRank,
                };
                newEvent.Submit(clock);
                await submit(newEvent, ct);
                created++;
                continue;
            }

            var existing = existingEvents.First(eventRow => eventRow.Id == eventId);
            // Compare recurrence by day-name set, so a lossless offsets→names round-trip
            // does not unnecessarily re-queue an unchanged event for moderation.
            var existingDays = existing.IsRecurring && !string.IsNullOrEmpty(existing.RecurrenceDays)
                ? EventRecurrenceDays.OffsetsToDisplayDays(existing.RecurrenceDays, gateOpeningDate)
                : string.Empty;
            var rowDays = row.IsRecurring ? row.RecurrenceDays ?? string.Empty : string.Empty;
            var changed = !string.Equals(existing.Title, row.Title, StringComparison.Ordinal)
                || !string.Equals(existing.Description, row.Description, StringComparison.Ordinal)
                || existing.CategoryId != category.Id || existing.StartAt != startAt
                || existing.DurationMinutes != row.DurationMinutes
                || !string.Equals(existing.LocationNote ?? string.Empty, row.LocationNote ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(existing.Host ?? string.Empty, row.Host ?? string.Empty, StringComparison.Ordinal)
                || existing.IsRecurring != row.IsRecurring || !EventRecurrenceDays.SameDays(existingDays, rowDays)
                || existing.PriorityRank != row.PriorityRank;
            if (!changed) continue;

            existing.Title = row.Title;
            existing.Description = row.Description;
            existing.CategoryId = category.Id;
            existing.StartAt = startAt;
            existing.DurationMinutes = row.DurationMinutes;
            existing.LocationNote = string.IsNullOrEmpty(row.LocationNote) ? null : row.LocationNote;
            existing.Host = string.IsNullOrEmpty(row.Host) ? null : row.Host;
            existing.IsRecurring = row.IsRecurring;
            existing.RecurrenceDays = row.IsRecurring ? recurrenceOffsets : null;
            existing.PriorityRank = row.PriorityRank;
            // The lifecycle service preserves Pending, re-queues Approved, and submits
            // Draft/Rejected/ResubmitRequested rows; validation excluded Withdrawn.
            await updateAndResubmit(existing, ct);
            updated++;
        }

        return new BulkImportResult([], created, updated);
    }
}
