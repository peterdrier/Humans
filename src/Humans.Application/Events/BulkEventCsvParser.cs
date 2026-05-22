using System.Globalization;
using System.Text;
using Humans.Application.DTOs.Events;

namespace Humans.Application.Events;

/// <summary>
/// Parses a barrio bulk-upload CSV (the format produced by the download
/// template) into <see cref="BulkCsvRow"/> records. Comment lines (starting
/// with <c>#</c>) and blank lines are skipped; the first remaining line is the
/// header. Throws <see cref="FormatException"/> on malformed input.
/// </summary>
public static class BulkEventCsvParser
{
    private const int ExpectedColumns = 14;

    public static List<BulkCsvRow> Parse(string csvText)
    {
        var rows = new List<BulkCsvRow>();
        var lines = csvText.Split('\n', StringSplitOptions.None);
        var dataLineNumber = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;

            var fields = SplitCsvLine(line);

            // First non-comment line is the header — skip it.
            if (dataLineNumber == 0)
            {
                dataLineNumber++;
                continue;
            }

            if (fields.Count < ExpectedColumns)
                throw new FormatException($"Row {dataLineNumber + 1}: expected {ExpectedColumns} columns, got {fields.Count}.");

            // fields[1] = Barrio (ignored), fields[2] = Status (ignored)
            Guid? id = string.IsNullOrWhiteSpace(fields[0])
                ? null
                : Guid.TryParse(fields[0], out var g) ? g : throw new FormatException($"Row {dataLineNumber + 1}: Id is not a valid Guid.");

            if (!int.TryParse(fields[8], CultureInfo.InvariantCulture, out var duration))
                throw new FormatException($"Row {dataLineNumber + 1}: DurationMinutes is not an integer.");
            if (!int.TryParse(fields[13], CultureInfo.InvariantCulture, out var priority))
                throw new FormatException($"Row {dataLineNumber + 1}: PriorityRank is not an integer.");

            var isRecurring = string.Equals(fields[11], "true", StringComparison.OrdinalIgnoreCase);

            rows.Add(new BulkCsvRow(
                dataLineNumber + 1, id,
                fields[3], fields[4], fields[5], fields[6], fields[7], duration,
                string.IsNullOrWhiteSpace(fields[9]) ? null : fields[9],
                string.IsNullOrWhiteSpace(fields[10]) ? null : fields[10],
                isRecurring,
                string.IsNullOrWhiteSpace(fields[12]) ? null : fields[12],
                priority));
            dataLineNumber++;
        }

        return rows;
    }

    /// <summary>RFC-4180 single-line split: handles quoted fields and <c>""</c> escapes.</summary>
    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var i = 0;
        while (i <= line.Length)
        {
            if (i == line.Length) { fields.Add(string.Empty); break; }
            if (line[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        i++;
                        if (i < line.Length && line[i] == '"') { sb.Append('"'); i++; }
                        else break;
                    }
                    else { sb.Append(line[i]); i++; }
                }
                fields.Add(sb.ToString());
                if (i < line.Length && line[i] == ',') i++;
            }
            else
            {
                var end = line.IndexOf(',', i);
                if (end < 0) { fields.Add(line[i..]); break; }
                fields.Add(line[i..end]);
                i = end + 1;
            }
        }
        return fields;
    }
}
