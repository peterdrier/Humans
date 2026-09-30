using Humans.Base.Extensions;
using Humans.Events.Contracts;
using Humans.Events.Domain;
using Humans.Events.Services.Dtos;

namespace Humans.Events.Services;

/// <summary>Validation boundary for the all-or-nothing barrio bulk-upload format.</summary>
internal static class EventBulkImportValidator
{
    public static List<BulkImportRowError> ValidateRows(
        IReadOnlyList<BulkCsvRow> rows,
        IReadOnlyList<EventCategory> categories,
        IReadOnlyList<Event> existingEvents)
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

            if (string.IsNullOrWhiteSpace(row.Title)) rowErrors.Add("Title is required.");
            else if (row.Title.Length > 80) rowErrors.Add("Title must be 80 characters or fewer.");

            if (string.IsNullOrWhiteSpace(row.Description)) rowErrors.Add("Description is required.");
            else if (row.Description.Length > 450) rowErrors.Add("Description must be 450 characters or fewer.");

            if (row.LocationNote?.Length > 120) rowErrors.Add("LocationNote must be 120 characters or fewer.");
            if (row.Host?.Length > 40) rowErrors.Add("Host must be 40 characters or fewer.");

            ValidateCategory(row, categories, rowErrors);

            if (string.IsNullOrWhiteSpace(row.Date)) rowErrors.Add("Date is required.");
            else if (!NodaTime.Text.LocalDatePattern.Iso.Parse(row.Date).Success)
                rowErrors.Add("Date must be in yyyy-MM-dd format.");

            if (string.IsNullOrWhiteSpace(row.StartTime)) rowErrors.Add("StartTime is required.");
            else if (!DateFormattingExtensions.TimeOfDayPattern.Parse(row.StartTime).Success)
                rowErrors.Add("StartTime must be in HH:mm format.");

            if (row.DurationMinutes < 15 || row.DurationMinutes > 480)
                rowErrors.Add("DurationMinutes must be between 15 and 480.");
            else if (row.DurationMinutes % 15 != 0)
                rowErrors.Add("DurationMinutes must be a multiple of 15.");

            if (row.PriorityRank is { } rank && (rank < 1 || rank > 100))
                rowErrors.Add("PriorityRank must be between 1 and 100.");

            if (row.IsRecurring && !string.IsNullOrWhiteSpace(row.RecurrenceDays)
                && !EventRecurrenceDays.HasOnlyDisplayDays(row.RecurrenceDays))
                rowErrors.Add("RecurrenceDays must contain only Mon Tue Wed Thu Fri Sat Sun.");

            ValidateExistingEvent(row, existingEvents, duplicateIds, rowErrors);

            if (rowErrors.Count > 0)
                errors.Add(new BulkImportRowError(row.RowNumber, row.Title, rowErrors));
        }
        return errors;
    }

    private static void ValidateCategory(
        BulkCsvRow row, IReadOnlyList<EventCategory> categories, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(row.Category))
        {
            errors.Add("Category is required.");
            return;
        }

        var matchingCategories = categories
            .Where(category => string.Equals(category.Name, row.Category, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matchingCategories.Count == 0)
            errors.Add($"Category '{row.Category}' is not a valid active category.");
        else if (matchingCategories.Count > 1)
            errors.Add($"Category '{row.Category}' matches more than one active category.");
    }

    private static void ValidateExistingEvent(
        BulkCsvRow row, IReadOnlyList<Event> existingEvents, HashSet<Guid> duplicateIds, List<string> errors)
    {
        if (!row.Id.HasValue) return;

        if (duplicateIds.Contains(row.Id.Value))
            errors.Add($"Event {row.Id.Value} appears more than once in the upload.");
        else if (existingEvents.FirstOrDefault(eventRow => eventRow.Id == row.Id.Value) is not { } existing)
            errors.Add($"Event {row.Id.Value} not found for this barrio.");
        else if (existing.Status == EventStatus.Withdrawn)
            errors.Add("Withdrawn events cannot be updated via bulk upload.");
    }
}
