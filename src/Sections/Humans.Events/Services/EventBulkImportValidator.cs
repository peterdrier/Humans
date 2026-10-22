using Microsoft.Extensions.Localization;
using Humans.Base.Extensions;
using Humans.Events.Contracts;
using Humans.Events.Domain;
using Humans.Events.Services.Dtos;
using NodaTime;
using NodaTime.Text;

namespace Humans.Events.Services;

/// <summary>All or nothing.</summary>
internal static class EventBulkImportValidator
{
    public static List<BulkImportRowError> ValidateRows(
        IReadOnlyList<BulkCsvRow> rows,
        IReadOnlyList<EventCategory> categories,
        IReadOnlyList<Event> existingEvents,
        DateTimeZone timeZone,
        IStringLocalizer<EventsResource> localizer)
    {
        var errors = new List<BulkImportRowError>();
        var duplicateIds = rows
            .Where(row => row.Id.HasValue)
            .GroupBy(row => row.Id!.Value)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        foreach (var row in rows)
        {
            var rowErrors = new List<string>();

            if (string.IsNullOrWhiteSpace(row.Title)) rowErrors.Add(localizer["Events_Upload_TitleRequired"].Value);
            else if (row.Title.Length > 80) rowErrors.Add(localizer["Events_Upload_TitleTooLong"].Value);

            if (string.IsNullOrWhiteSpace(row.Description)) rowErrors.Add(localizer["Events_Upload_DescriptionRequired"].Value);
            else if (row.Description.Length > 450) rowErrors.Add(localizer["Events_Upload_DescriptionTooLong"].Value);

            if (row.LocationNote?.Length > 120) rowErrors.Add(localizer["Events_Upload_LocationNoteTooLong"].Value);
            if (row.Host?.Length > 40) rowErrors.Add(localizer["Events_Upload_HostTooLong"].Value);

            ValidateCategory(row, categories, rowErrors, localizer);

            var date = LocalDatePattern.Iso.Parse(row.Date ?? string.Empty);
            var time = DateFormattingExtensions.TimeOfDayPattern.Parse(row.StartTime ?? string.Empty);
            if (string.IsNullOrWhiteSpace(row.Date)) rowErrors.Add(localizer["Events_Upload_DateRequired"].Value);
            else if (!date.Success)
                rowErrors.Add(localizer["Events_Upload_DateFormat"].Value);

            if (string.IsNullOrWhiteSpace(row.StartTime)) rowErrors.Add(localizer["Events_Upload_StartTimeRequired"].Value);
            else if (!time.Success)
                rowErrors.Add(localizer["Events_Upload_StartTimeFormat"].Value);

            if (row.DurationMinutes < 15 || row.DurationMinutes > 480)
                rowErrors.Add(localizer["Events_Upload_DurationRange"].Value);
            else if (row.DurationMinutes % 15 != 0)
                rowErrors.Add(localizer["Events_Upload_DurationIncrement"].Value);

            if (date.Success && time.Success && row.DurationMinutes is >= 15 and <= 480
                && !CanDisplaySchedule(date.Value + time.Value, row.DurationMinutes, timeZone))
                rowErrors.Add(localizer["Events_Upload_DateRange"].Value);

            if (row.PriorityRank is { } rank && (rank < 1 || rank > 100))
                rowErrors.Add(localizer["Events_Upload_PriorityRange"].Value);

            if (row.IsRecurring && !string.IsNullOrWhiteSpace(row.RecurrenceDays)
                && !EventRecurrenceDays.HasOnlyDisplayDays(row.RecurrenceDays))
                rowErrors.Add(localizer["Events_Upload_RecurrenceDays"].Value);

            ValidateExistingEvent(row, existingEvents, duplicateIds, rowErrors, localizer);

            if (rowErrors.Count > 0)
                errors.Add(new BulkImportRowError(row.RowNumber, row.Title, rowErrors));
        }
        return errors;
    }

    private static bool CanDisplaySchedule(LocalDateTime localStart, int durationMinutes, DateTimeZone timeZone)
    {
        // CSV accepts NodaTime dates beyond the DateTime range used by calendars and views.
        try
        {
            var start = localStart.InZoneLeniently(timeZone).ToInstant();
            var end = start + Duration.FromMinutes(durationMinutes);
            return start.InUtc().Year >= 1 && start.InZone(timeZone).Year >= 1
                && end.InUtc().Year >= 1 && end.InZone(timeZone).Year >= 1;
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static void ValidateCategory(
        BulkCsvRow row, IReadOnlyList<EventCategory> categories, List<string> errors,
        IStringLocalizer<EventsResource> localizer)
    {
        if (string.IsNullOrWhiteSpace(row.Category))
        {
            errors.Add(localizer["Events_Upload_CategoryRequired"].Value);
            return;
        }

        var matchingCategories = categories
            .Where(category => string.Equals(category.Name, row.Category, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matchingCategories.Count == 0)
            errors.Add(localizer["Events_Upload_CategoryInvalid", row.Category].Value);
        else if (matchingCategories.Count > 1)
            errors.Add(localizer["Events_Upload_CategoryAmbiguous", row.Category].Value);
    }

    private static void ValidateExistingEvent(
        BulkCsvRow row, IReadOnlyList<Event> existingEvents, HashSet<Guid> duplicateIds, List<string> errors,
        IStringLocalizer<EventsResource> localizer)
    {
        if (!row.Id.HasValue) return;

        if (duplicateIds.Contains(row.Id.Value))
            errors.Add(localizer["Events_Upload_DuplicateEventId", row.Id.Value].Value);
        else if (existingEvents.FirstOrDefault(eventRow => eventRow.Id == row.Id.Value) is not { } existing)
            errors.Add(localizer["Events_Upload_EventNotFound", row.Id.Value].Value);
        else if (existing.Status == EventStatus.Withdrawn)
            errors.Add(localizer["Events_Upload_WithdrawnEventCannotUpdate"].Value);
    }
}
