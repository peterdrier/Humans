using System.Runtime.CompilerServices;
using System.Text;
using CsvHelper;

namespace Humans.Base.Csv;

/// <summary>
/// The one place CSV conventions are defined: every CSV read or write builds its reader or writer from these
/// factories, and hand-rolled splitting, quoting or escaping is not allowed (<c>memory/code/csv-use-csvhelper.md</c>).
/// </summary>
public static class HumansCsv
{
    /// <summary>UTF-8 with BOM — Excel needs the BOM to detect UTF-8 in downloads.</summary>
    public static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    // CsvHelper 34 refuses zero-field records, so blank separator lines go straight to the text writer.
    private static readonly ConditionalWeakTable<CsvWriter, TextWriter> Destinations = new();

    /// <summary>
    /// Reading defaults for user-supplied files: invariant culture, delimiter
    /// detection (Spanish/European Excel saves semicolon-delimited CSV),
    /// whitespace-and-case-forgiving header matching, trimmed fields.
    /// Malformed quoting throws <see cref="CsvFormatException"/> rather than being guessed at.
    /// Call sites adjust it with <c>with</c> (e.g. <c>CommentPrefix</c>).
    /// </summary>
    public static CsvReaderOptions ReadConfig() => new()
    {
        Separator = null,
        NormalizeHeaderName = header => header.Trim().ToLowerInvariant(),
        TrimWhitespace = true,
        MalformedData = CsvMalformedDataHandling.Throw,
    };

    /// <summary>
    /// Writing defaults for exports: invariant culture, RFC 4180 CRLF, OWASP CSV-injection escaping
    /// (CsvHelper's default set includes <c>\t</c> and <c>\r</c>), and quoting of any field holding CR or LF.
    /// Field counts may vary between records: several exports write titled sections and summary rows.
    /// </summary>
    public static CsvWriterOptions WriteConfig() => new()
    {
        NewLine = "\r\n",
        Injection = CsvInjection.Escape,
        InjectionEscape = '\'',
        ValidateFieldCount = false,
    };

    /// <summary>
    /// Builds a UTF-8 (BOM) CSV byte payload — the standard body for a
    /// <c>text/csv</c> file result. <paramref name="configure"/> may return an
    /// adjusted copy of the write configuration (via <c>with</c>) before the writer is created.
    /// </summary>
    public static byte[] WriteBytes(Action<CsvWriter> write, Func<CsvWriterOptions, CsvWriterOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(write);

        var config = WriteConfig();
        if (configure is not null) config = configure(config);

        using var ms = new MemoryStream();
        using (var sw = new StreamWriter(ms, Utf8WithBom))
        using (var csv = CsvWriter.Create(sw, config, leaveOpen: false))
        {
            Destinations.Add(csv, sw);
            write(csv);
        }

        return ms.ToArray();
    }

    /// <summary>Writes one record from loose values (nulls become empty cells) and ends the row.</summary>
    public static void WriteRow(this CsvWriter csv, params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(csv);

        foreach (var value in values)
        {
            if (value is null) csv.WriteField((string?)null);
            else csv.WriteField(value);
        }

        csv.EndRecord();
    }

    /// <summary>Writes an empty line between sections of a <see cref="WriteBytes"/> payload.</summary>
    public static void WriteBlankLine(this CsvWriter csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        if (!Destinations.TryGetValue(csv, out var destination))
            throw new InvalidOperationException("Blank lines are only supported on writers created by WriteBytes.");

        csv.Flush();
        destination.Write(csv.Options.NewLine);
    }
}
