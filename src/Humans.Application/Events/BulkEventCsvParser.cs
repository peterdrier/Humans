using System.Globalization;
using CsvHelper;
using Humans.Application.Csv;
using Humans.Application.DTOs.Events;

namespace Humans.Application.Events;

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
public static class BulkEventCsvParser
{
    public static List<BulkCsvRow> Parse(string csvText)
    {
        var config = HumansCsv.ReadConfig();
        config.AllowComments = true;
        config.Comment = '#';
        // CsvHelper's DetectDelimiter samples the comment banner too — its
        // comma-rich prose outvotes semicolon data rows (the Spanish-Excel
        // re-save). Sniff the header line instead.
        config.DetectDelimiter = false;
        config.Delimiter = SniffDelimiter(csvText);
        // Ragged rows read missing trailing cells as empty instead of throwing;
        // genuinely absent columns are caught by header validation below.
        config.MissingFieldFound = null;
        config.HeaderValidated = args =>
        {
            if (args.InvalidHeaders.Length == 0) return;
            var missing = string.Join(", ", args.InvalidHeaders.SelectMany(h => h.Names));
            throw new FormatException($"The CSV is missing required column(s): {missing}. Download a fresh template to see the expected columns.");
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
                else errors.Add($"Row {fileRow}: Id is not a valid Guid.");
            }

            if (!int.TryParse(record.DurationMinutes, CultureInfo.InvariantCulture, out var duration))
                errors.Add($"Row {fileRow}: DurationMinutes is not an integer.");
            if (!int.TryParse(record.PriorityRank, CultureInfo.InvariantCulture, out var priority))
                errors.Add($"Row {fileRow}: PriorityRank is not an integer.");

            var isRecurring = string.Equals(record.IsRecurring, "true", StringComparison.OrdinalIgnoreCase);

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
    private static string SniffDelimiter(string csvText)
    {
        foreach (var rawLine in csvText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            return line.Count(c => c == ';') > line.Count(c => c == ',') ? ";" : ",";
        }
        return ",";
    }
}
