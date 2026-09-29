using System.Globalization;
using CsvHelper.Configuration;
using Humans.Base.Csv;
using Humans.Base.Extensions;
using Humans.Events.Contracts;
using Humans.Events.Services.Dtos;
using NodaTime;

namespace Humans.Events.Services;

/// <summary>Renders the downloadable barrio bulk-upload CSV from already-loaded event data.</summary>
internal static class EventBulkUploadTemplateBuilder
{
    public static byte[] Build(
        string campName, IReadOnlyList<EventInfo> campEvents, IReadOnlyList<EventCategoryView> categories,
        DateTimeZone? timeZone, LocalDate? gateDate, IClock clock)
    {
        var categoryNames = string.Join(", ", categories.Select(category => category.Name));
        var banner = new[]
        {
            " ─────────────────────────────────────────────────────────────────────────────",
            " ELSEWHERE EVENT GUIDE — Bulk Upload Template",
            " ─────────────────────────────────────────────────────────────────────────────",
            "",
            " HOW TO USE",
            "   1. Fill in new rows leaving Id blank — a new event will be created.",
            "   2. Existing rows already have an Id filled in. You may edit their fields,",
            "      but DO NOT change or delete the Id — that is how we match the event.",
            "      Changing an Id will cause the upload to fail.",
            "   3. To leave an existing event unchanged, keep its row as-is.",
            "      Events not present in the CSV are left untouched.",
            "   4. Save as CSV (UTF-8) before uploading. Columns may be in any order and",
            "      extra columns are ignored — match the column names, not the layout.",
            "      In Excel:   File → Save As → CSV UTF-8 (Comma delimited)",
            "      In Numbers: File → Export To → CSV",
            "",
            " FIELDS",
            "   Id             Leave empty for new events. Do not edit for existing ones.",
            "   Barrio         Informational only — shows which camp this file belongs to. Ignored on upload.",
            "   Status         Informational only — shows the current event status. Ignored on upload.",
            "                  If you upload a row without changing any fields, the status is kept as-is.",
            "                  If you edit fields on an existing event, it will be re-queued for moderation.",
            "   Category       Must match exactly one of the valid categories listed below.",
            "   Date           Format: yyyy-MM-dd  (e.g. 2026-07-08)",
            "   StartTime      Format: HH:mm       (e.g. 09:30)",
            "   DurationMinutes  Integer, 15–480, in 15-minute increments (e.g. 15, 30, 45, 60, 90, 120...).",
            "   IsRecurring    true or false.",
            "   RecurrenceDays  Only used when IsRecurring is true.",
            "                  Space-separated day names: Mon Tue Wed Thu Fri Sat Sun",
            "                  Example: Mon Wed Fri means the event repeats on those days.",
            "",
            " VALID CATEGORIES",
            $"   {categoryNames}",
            "",
            " ─────────────────────────────────────────────────────────────────────────────",
        };
        var nonWithdrawn = campEvents
            .Where(eventRow => eventRow.Status != EventStatus.Withdrawn)
            .OrderByDescending(eventRow => eventRow.SubmittedAt)
            .ToList();
        var records = nonWithdrawn.Select(eventRow => new BulkEventCsvRecord
        {
            Id = eventRow.Id.ToString("D", CultureInfo.InvariantCulture),
            Barrio = campName,
            Status = eventRow.Status.ToString(),
            Title = eventRow.Title,
            Description = eventRow.Description,
            Category = eventRow.CategoryName,
            Date = ToLocalDateTime(eventRow.StartAt, timeZone).ToInvariantDate(),
            StartTime = ToLocalDateTime(eventRow.StartAt, timeZone).ToInvariantTime(),
            DurationMinutes = eventRow.DurationMinutes.ToString(CultureInfo.InvariantCulture),
            LocationNote = eventRow.LocationNote ?? string.Empty,
            Host = eventRow.Host ?? string.Empty,
            IsRecurring = eventRow.IsRecurring ? "true" : "false",
            RecurrenceDays = eventRow.IsRecurring && !string.IsNullOrEmpty(eventRow.RecurrenceDays) && gateDate.HasValue
                ? EventRecurrenceDays.OffsetsToDisplayDays(eventRow.RecurrenceDays, gateDate.Value)
                : string.Empty,
            PriorityRank = eventRow.PriorityRank?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        }).ToList();
        if (records.Count == 0)
        {
            records.Add(new BulkEventCsvRecord
            {
                Barrio = campName,
                Title = "Example Event",
                Description = "Describe your event here.",
                Category = categories.FirstOrDefault()?.Name ?? "Workshop",
                Date = (gateDate ?? clock.GetCurrentInstant().InZone(timeZone ?? DateTimeZone.Utc).Date).ToInvariantDate(),
                StartTime = "12:00",
                DurationMinutes = "60",
                IsRecurring = "false",
                PriorityRank = "1",
            });
        }

        return HumansCsv.WriteBytes(
            csv =>
            {
                csv.Context.RegisterClassMap<BulkEventCsvRecordMap>();
                foreach (var line in banner)
                {
                    csv.WriteComment(line);
                    csv.NextRecord();
                }
                csv.WriteRecords(records);
            },
            config => config.InjectionOptions = InjectionOptions.None);
    }

    private static DateTime ToLocalDateTime(Instant instant, DateTimeZone? timeZone) =>
        timeZone is null ? instant.ToDateTimeUtc() : instant.InZone(timeZone).ToDateTimeUnspecified();
}
