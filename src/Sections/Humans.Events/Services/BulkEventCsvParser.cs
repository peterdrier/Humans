using System.Globalization;
using CsvHelper;
using Humans.Base.Csv;
using Humans.Events.Services.Dtos;

namespace Humans.Events.Services;

/// <summary>
/// Columns match by header name in any order; unknown columns are ignored, <c>#</c> comment lines skipped, and quoted
/// fields may span lines. The delimiter is detected because Spanish Excel saves semicolons. Any header or cell refusal
/// leaves no row importable.
/// </summary>
internal static class BulkEventCsvParser
{
    public static BulkCsvParseResult Parse(string csvText)
    {
        var errors = new List<BulkCsvParseError>();
        var config = HumansCsv.ReadConfig() with { CommentPrefix = '#', TrackLineNumbers = true };

        var rows = new List<BulkCsvRow>();

        using var csv = CsvReader.FromText(csvText, config);

        if (!csv.TryValidateHeader<BulkEventCsvRecord>(out var missing))
            return new([], [new("Events_Upload_MissingRequiredColumns", string.Join(", ", missing.Select(HeaderName)))]);

        while (csv.Read())
        {
            var record = csv.GetRecord<BulkEventCsvRecord>();
            var fileRow = (int)csv.LineNumber;

            Guid? id = null;
            if (!string.IsNullOrWhiteSpace(record.Id))
            {
                if (Guid.TryParse(record.Id, out var g)) id = g;
                else errors.Add(new("Events_Upload_RowIdInvalidGuid", fileRow));
            }

            if (!int.TryParse(record.DurationMinutes, CultureInfo.InvariantCulture, out var duration))
                errors.Add(new("Events_Upload_RowDurationNotInteger", fileRow));
            int? priority = null;
            if (!string.IsNullOrWhiteSpace(record.PriorityRank))
            {
                if (int.TryParse(record.PriorityRank, CultureInfo.InvariantCulture, out var parsedPriority)) priority = parsedPriority;
                else errors.Add(new("Events_Upload_RowPriorityNotInteger", fileRow));
            }

            var isRecurring = false;
            if (!string.IsNullOrWhiteSpace(record.IsRecurring) && !bool.TryParse(record.IsRecurring, out isRecurring))
                errors.Add(new("Events_Upload_RowRecurringBoolean", fileRow));

            rows.Add(new BulkCsvRow(
                fileRow, id,
                record.Title, record.Description, record.Category, record.Date, record.StartTime, duration,
                string.IsNullOrWhiteSpace(record.LocationNote) ? null : record.LocationNote,
                string.IsNullOrWhiteSpace(record.Host) ? null : record.Host,
                isRecurring,
                string.IsNullOrWhiteSpace(record.RecurrenceDays) ? null : record.RecurrenceDays,
                priority));
        }

        return new(errors.Count == 0 ? rows : [], errors);
    }

    /// <summary>CsvHelper names a missing column as <c>Type.Member ('Header')</c>; uploaders only know the header.</summary>
    private static string HeaderName(string missingColumn)
    {
        var open = missingColumn.IndexOf("('", StringComparison.Ordinal);
        return open >= 0 && missingColumn.EndsWith("')", StringComparison.Ordinal)
            ? missingColumn[(open + 2)..^2]
            : missingColumn;
    }
}
