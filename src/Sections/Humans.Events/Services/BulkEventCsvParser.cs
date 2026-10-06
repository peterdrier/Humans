using System.Globalization;
using Microsoft.Extensions.Localization;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Delegates;
using Humans.Base.Csv;
using Humans.Events.Services.Dtos;

namespace Humans.Events.Services;

/// <summary>
/// Parses a barrio bulk-upload CSV (the format produced by the download
/// template) into <see cref="BulkCsvRow"/> records via CsvHelper and
/// <see cref="BulkEventCsvRecordMap"/>. Columns are matched by header name in
/// any order, unknown columns are ignored, the delimiter is auto-detected
/// (Spanish Excel saves semicolons), comment lines (starting with <c>#</c>)
/// are skipped, and quoted fields may span lines. Throws
/// <see cref="FormatException"/> with ALL row errors (real file row numbers)
/// on malformed input.
/// </summary>
internal static class BulkEventCsvParser
{
    public static List<BulkCsvRow> Parse(string csvText, IStringLocalizer<EventsResource> localizer)
    {
        var config = HumansCsv.ReadConfig();
        config.AllowComments = true;
        config.Comment = '#';
        // CsvHelper's DetectDelimiter samples the comment banner too — its
        // comma-rich prose outvotes semicolon data rows (the Spanish-Excel
        // re-save). Sniff the header line instead.
        config.DetectDelimiter = false;
        config.DetectDelimiterValues = [",", ";"];
        config.Delimiter = SniffDelimiter(csvText, config);
        // Ragged rows read missing trailing cells as empty instead of throwing;
        // genuinely absent columns are caught by header validation below.
        config.MissingFieldFound = null;
        config.HeaderValidated = args =>
        {
            if (args.InvalidHeaders.Length == 0) return;
            var missing = string.Join(", ", args.InvalidHeaders.SelectMany(h => h.Names));
            throw new FormatException(localizer["Events_Upload_MissingRequiredColumns", missing].Value);
        };

        var rows = new List<BulkCsvRow>();
        var errors = new List<string>();

        using var reader = new StringReader(csvText);
        using var csv = new CsvReader(reader, config);
        csv.Context.RegisterClassMap<BulkEventCsvRecordMap>();

        if (!csv.Read()) return rows;
        csv.ReadHeader();
        csv.ValidateHeader<BulkEventCsvRecord>();

        while (csv.Read())
        {
            var record = csv.GetRecord<BulkEventCsvRecord>();
            var fileRow = csv.Parser.RawRow;

            Guid? id = null;
            if (!string.IsNullOrWhiteSpace(record.Id))
            {
                if (Guid.TryParse(record.Id, out var g)) id = g;
                else errors.Add(localizer["Events_Upload_RowIdInvalidGuid", fileRow].Value);
            }

            if (!int.TryParse(record.DurationMinutes, CultureInfo.InvariantCulture, out var duration))
                errors.Add(localizer["Events_Upload_RowDurationNotInteger", fileRow].Value);
            int? priority = null;
            if (!string.IsNullOrWhiteSpace(record.PriorityRank))
            {
                if (int.TryParse(record.PriorityRank, CultureInfo.InvariantCulture, out var parsedPriority)) priority = parsedPriority;
                else errors.Add(localizer["Events_Upload_RowPriorityNotInteger", fileRow].Value);
            }

            var isRecurring = false;
            if (!string.IsNullOrWhiteSpace(record.IsRecurring) && !bool.TryParse(record.IsRecurring, out isRecurring))
                errors.Add(localizer["Events_Upload_RowRecurringBoolean", fileRow].Value);

            rows.Add(new BulkCsvRow(
                fileRow, id,
                record.Title, record.Description, record.Category, record.Date, record.StartTime, duration,
                string.IsNullOrWhiteSpace(record.LocationNote) ? null : record.LocationNote,
                string.IsNullOrWhiteSpace(record.Host) ? null : record.Host,
                isRecurring,
                string.IsNullOrWhiteSpace(record.RecurrenceDays) ? null : record.RecurrenceDays,
                priority));
        }

        if (errors.Count > 0)
            throw new FormatException(string.Join(" ", errors));

        return rows;
    }

    /// <summary>Delimiter of the header line — the first non-comment, non-blank line.</summary>
    private static string SniffDelimiter(string csvText, CsvConfiguration config)
    {
        foreach (var rawLine in csvText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            return config.GetDelimiter(new GetDelimiterArgs(line, config));
        }
        return ",";
    }
}
