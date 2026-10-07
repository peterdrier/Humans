using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Events.Data;
using Humans.Events.Domain;
using Humans.Events.Services.Dtos;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Events.Services;

/// <summary>Validates the whole CSV before applying changed rows to a barrio's events.</summary>
internal sealed class EventBulkImporter(
    IEventRepository repo,
    IClock clock,
    IStringLocalizer<EventsResource> localizer) : IApplicationService
{
    public async Task<BulkImportResult> ImportAsync(
        Guid campId, Guid submitterUserId, IReadOnlyList<BulkCsvRow> rows,
        LocalDate gateOpeningDate, int eventEndOffset, DateTimeZone timeZone,
        CancellationToken ct = default)
    {
        var categories = await repo.GetActiveCategoriesAsync(ct);
        var existingEvents = await repo.GetCampSubmissionsAsync(campId, ct);

        var errors = EventBulkImportValidator.ValidateRows(rows, categories, existingEvents, localizer);
        if (errors.Count > 0)
            return new BulkImportResult(errors, 0, 0);

        var created = 0;
        var updated = 0;
        foreach (var row in rows)
        {
            var category = categories.First(c => string.Equals(c.Name, row.Category, StringComparison.OrdinalIgnoreCase));
            var date = NodaTime.Text.LocalDatePattern.Iso.Parse(row.Date).Value;
            var time = DateFormattingExtensions.TimeOfDayPattern.Parse(row.StartTime).Value;
            var startAt = (date + time).InZoneLeniently(timeZone).ToInstant();
            var recurrenceOffsets = row.IsRecurring && !string.IsNullOrEmpty(row.RecurrenceDays)
                ? EventRecurrenceDays.DisplayDaysToOffsets(row.RecurrenceDays, gateOpeningDate, eventEndOffset)
                : null;

            var target = row.Id.HasValue
                ? existingEvents.First(e => e.Id == row.Id.Value)
                : new Event
                {
                    Id = Guid.NewGuid(),
                    CampId = campId,
                    SubmitterUserId = submitterUserId
                };
            var recurrenceChanged = true;
            if (row.Id.HasValue)
            {
                // Compare recurrence by day-name set, not the raw offset string, so a
                // lossless round-trip ("0" ⇄ "Mon") isn't mistaken for an edit and the
                // event isn't needlessly re-queued for moderation.
                var existingDays = target.IsRecurring && !string.IsNullOrEmpty(target.RecurrenceDays)
                    ? EventRecurrenceDays.OffsetsToDisplayDays(target.RecurrenceDays, gateOpeningDate)
                    : string.Empty;
                var rowDays = row.IsRecurring ? row.RecurrenceDays ?? string.Empty : string.Empty;
                recurrenceChanged = target.IsRecurring != row.IsRecurring
                    || !EventRecurrenceDays.SameDays(existingDays, rowDays);

                var changed =
                    !string.Equals(target.Title, row.Title, StringComparison.Ordinal) ||
                    !string.Equals(target.Description, row.Description, StringComparison.Ordinal) ||
                    target.CategoryId != category.Id ||
                    target.StartAt != startAt ||
                    target.DurationMinutes != row.DurationMinutes ||
                    !string.Equals(target.LocationNote ?? string.Empty, row.LocationNote ?? string.Empty, StringComparison.Ordinal) ||
                    !string.Equals(target.Host ?? string.Empty, row.Host ?? string.Empty, StringComparison.Ordinal) ||
                    recurrenceChanged ||
                    target.PriorityRank != row.PriorityRank;

                if (!changed) continue;
            }

            target.Title = row.Title;
            target.Description = row.Description;
            target.CategoryId = category.Id;
            target.StartAt = startAt;
            target.DurationMinutes = row.DurationMinutes;
            target.LocationNote = string.IsNullOrEmpty(row.LocationNote) ? null : row.LocationNote;
            target.Host = string.IsNullOrEmpty(row.Host) ? null : row.Host;
            target.IsRecurring = row.IsRecurring;
            // Unchanged weekday labels cannot express a subset of repeated weekdays.
            // Preserve existing authored offsets when another field is edited.
            if (recurrenceChanged || !row.IsRecurring)
                target.RecurrenceDays = row.IsRecurring ? recurrenceOffsets : null;
            target.PriorityRank = row.PriorityRank;

            if (row.Id.HasValue)
            {
                // Existing rows always update: even Draft/Rejected rows are already stored.
                target.Resubmit(clock);
                await repo.SaveEventAsync(target, ct);
                updated++;
            }
            else
            {
                target.Submit(clock);
                await repo.AddEventAsync(target, ct);
                created++;
            }
        }

        return new BulkImportResult([], created, updated);
    }
}
